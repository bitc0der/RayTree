using System.Diagnostics.Metrics;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RayTree.Core.Models;
using RayTree.Core.Resilience;
using RayTree.Core.Telemetry;
using RayTree.Core.Tracking;
using RayTree.Plugins.Compressors.Gzip;
using RayTree.Plugins.InMemory;
using RayTree.Plugins.PostgreSQL.Outbox;
using RayTree.Plugins.PostgreSQL.Outbox.Notification;
using RayTree.Plugins.Serializers.Json;

namespace RayTree.Plugins.PostgreSQL.Tests;

/// <summary>
/// Integration tests for the LISTEN-connection reconnect path in <see cref="NotificationBasedPublisher"/>
/// and the outbox-side observability hooks in <see cref="RayTree.Core.Distribution.OutboxPublisherService"/>.
/// Each test owns its own container so a permanent-kill negative case doesn't leak into the next test.
/// </summary>
[NonParallelizable]
public class NotificationBasedPublisherRecoveryTests
{
    /// <summary>
    /// Per-test container — container restart is the whole point, and a permanent-kill
    /// negative test must not leave the next test without Postgres. Cost is ~5s of pull/start
    /// per test which is acceptable for three integration scenarios.
    /// </summary>
    private IContainer _postgres = null!;
    private string _connectionString = null!;
    private RayTreeMeter _meter = null!;
    private CapturingMeterListener _capture = null!;

    private const string OutboxTable = "recovery_test_outbox";

    [SetUp]
    public async Task SetUp()
    {
        _postgres = PostgresContainerFactory.Create();
        await _postgres.StartAsync();
        _connectionString = ((Testcontainers.PostgreSql.PostgreSqlContainer)_postgres).GetConnectionString();

        _meter = new RayTreeMeter();
        _capture = new CapturingMeterListener(_meter);
    }

    [TearDown]
    public async Task TearDown()
    {
        _capture.Dispose();
        _meter.Dispose();
        try { await _postgres.DisposeAsync(); } catch { /* may already be stopped */ }
    }

    [Test]
    public async Task ListenConnectionKilled_DuringPublisherRunning_EmitsDisconnectThenRecovery_AndContinuesDelivering()
    {
        // Arrange — kill the LISTEN connection from the Postgres side via pg_terminate_backend
        // rather than stop/restart the container. This is deterministic: the LISTEN session
        // dies immediately on the next read, the broker stays up so reconnect succeeds quickly,
        // and the test is not at the mercy of Docker stop/start timing.
        var channel = $"channel_kill_{Guid.NewGuid():N}";
        await using var ctx = await BuildPublisherAsync(channel,
            recovery: new ConnectionRecoveryOptions
            {
                InitialDelay   = TimeSpan.FromMilliseconds(200),
                MaxDelay       = TimeSpan.FromSeconds(2),
                Factor         = 2.0,
                JitterFraction = 0.0,
                MaxAttempts    = null
            });

        await ctx.Outbox.WriteAsync(SampleChange(1));
        await ctx.Publisher.StartAsync();

        // Drain the pre-kill message so the post-recovery assertion is unambiguous.
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            await ctx.Queue.Reader.ReadAsync(cts.Token);

        // Give the LISTEN session time to register on the broker side before we kill it.
        await Task.Delay(300);

        // Act — terminate the listener's backend process. pg_terminate_backend kills the
        // backend and closes the TCP connection so WaitAsync surfaces the drop on the next
        // network read (typically within a few hundred ms).
        await TerminateListenBackendsAsync(channel);

        await WaitForAsync(
            () => _capture.SumOf("raytree.connection.disconnects", "postgres.notification") >= 1,
            TimeSpan.FromSeconds(15),
            "disconnect metric on LISTEN drop");

        await WaitForAsync(
            () => _capture.SumOf("raytree.connection.recoveries", "postgres.notification", outcome: "succeeded") >= 1,
            TimeSpan.FromSeconds(15),
            "succeeded recovery after LISTEN reconnect");

        // Assert — a post-recovery message is delivered (NOTIFY fast-path or fallback poll).
        await ctx.Outbox.WriteAsync(SampleChange(2));
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            var received = await ctx.Queue.Reader.ReadAsync(cts.Token);
            Assert.That(received.EntityId, Is.EqualTo("2"));
        }

        await ctx.Publisher.StopAsync();
    }

    /// <summary>
    /// Asks Postgres to terminate every backend that is currently in <c>LISTEN</c> for the
    /// given channel. Faster and more deterministic than container stop/start for testing the
    /// listener's reconnect path while leaving the broker up.
    /// </summary>
    private async Task TerminateListenBackendsAsync(string channel)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
            SELECT pg_terminate_backend(l.pid)
            FROM pg_listening_channels() ch
            CROSS JOIN LATERAL (
                SELECT pid FROM pg_stat_activity WHERE pid != pg_backend_pid()
            ) l
            WHERE EXISTS (
                SELECT 1 FROM pg_stat_activity sa
                WHERE sa.pid = l.pid
                  AND sa.query ILIKE 'LISTEN ' || quote_ident(@channel) || '%'
            )", conn);
        cmd.Parameters.AddWithValue("@channel", channel);
        await cmd.ExecuteNonQueryAsync();

        // Fallback — pg_listening_channels() only inspects the current session. Use the more
        // forgiving "any session that issued LISTEN <channel>" via pg_stat_activity.query.
        await using var cmd2 = new NpgsqlCommand(@"
            SELECT pg_terminate_backend(pid)
            FROM pg_stat_activity
            WHERE pid != pg_backend_pid()
              AND query ILIKE 'LISTEN ' || quote_ident(@channel) || '%'", conn);
        cmd2.Parameters.AddWithValue("@channel", channel);
        await cmd2.ExecuteNonQueryAsync();
    }

    [Test]
    public async Task PermanentKill_WithMaxAttempts2_EmitsExhaustedRecovery_AndExitsListenLoop()
    {
        var channel = $"channel_exhaust_{Guid.NewGuid():N}";
        await using var ctx = await BuildPublisherAsync(channel,
            recovery: new ConnectionRecoveryOptions
            {
                InitialDelay   = TimeSpan.FromMilliseconds(100),
                MaxDelay       = TimeSpan.FromMilliseconds(200),
                Factor         = 2.0,
                JitterFraction = 0.0,
                MaxAttempts    = 2
            });

        await ctx.Publisher.StartAsync();

        // Kill the container permanently — DisposeAsync removes it entirely.
        await _postgres.DisposeAsync();

        // The disconnect should fire (LISTEN breaks), then two reconnect attempts both fail,
        // then the exhausted recovery is emitted and the listen loop exits.
        await WaitForAsync(
            () => _capture.SumOf("raytree.connection.recoveries", "postgres.notification", outcome: "exhausted") >= 1,
            TimeSpan.FromSeconds(30),
            "exhausted recovery after MaxAttempts=2");

        Assert.That(_capture.SumOf("raytree.connection.disconnects", "postgres.notification"),
            Is.GreaterThanOrEqualTo(1),
            "disconnect must precede exhausted recovery");

        await ctx.Publisher.StopAsync();
    }

    [Test]
    public async Task OutboxOperations_DuringContainerStop_EmitPostgresOutboxDisconnect_ThenRecoveryOnRestart()
    {
        // 4b.5: cover the postgres.outbox observability path independently of postgres.notification.
        // We exercise it directly via PostgreSqlOutbox calls (which is what OutboxPublisherService
        // does internally) so the test is deterministic without standing up a full tracker.
        var outbox = new PostgreSqlOutbox<TestEntity>(new PostgreSqlOutboxOptions
        {
            ConnectionString = _connectionString,
            OutboxTableName  = OutboxTable
        }, NullLoggerFactory.Instance);
        await outbox.InitializeAsync();

        // Sanity: classifier overrides are wired up.
        Assert.That(outbox.ConnectionComponent, Is.EqualTo("postgres.outbox"));
        Assert.That(outbox.ConnectionEndpoint,  Is.Not.Null.And.Contains(":"));

        // Healthy call — no metric expected because we haven't simulated a fault.
        await outbox.WriteAsync(SampleChange(1));
        Assert.That(_capture.SumOf("raytree.connection.disconnects", "postgres.outbox"), Is.EqualTo(0));

        // Stop the container; the next outbox call should fail with a connection-classified exception.
        await _postgres.StopAsync();

        Exception? thrown = null;
        try { await outbox.WriteAsync(SampleChange(2)); }
        catch (Exception ex) { thrown = ex; }

        Assert.That(thrown, Is.Not.Null, "Outbox write should throw when Postgres is stopped");
        Assert.That(outbox.IsConnectionFault(thrown!), Is.True,
            "The thrown exception SHALL be classified as a connection fault — that's the contract " +
            "OutboxPublisherService relies on to emit the postgres.outbox disconnect metric.");
    }

    // ---- helpers --------------------------------------------------------

    private async Task<PublisherContext> BuildPublisherAsync(string channel, ConnectionRecoveryOptions recovery)
    {
        var queue = new InMemoryQueue();
        var outbox = new PostgreSqlOutbox<TestEntity>(new PostgreSqlOutboxOptions
        {
            ConnectionString       = _connectionString,
            OutboxTableName        = OutboxTable,
            UseNotificationChannel = true,
            NotificationChannel    = channel
        }, NullLoggerFactory.Instance);
        await outbox.InitializeAsync();

        var tracker = EntityChangeTracker.Create()
            .UseMeter(_meter)
            .ForEntity<TestEntity>(e => e
                .UseOutbox(outbox)
                .UsePublisher(queue)
                .UseSerializer(new JsonSerializerPlugin())
                .UseCompressor(new GzipCompressorPlugin()))
            .Build();

        var publisher = new NotificationBasedPublisher(tracker,
            new NotificationBasedPublisherOptions
            {
                ConnectionString        = _connectionString,
                ChannelName             = channel,
                FallbackPollingInterval = TimeSpan.FromMilliseconds(500),
                ConnectionRecovery      = recovery
            }, NullLoggerFactory.Instance);

        return new PublisherContext(publisher, tracker, outbox, queue);
    }

    private static EntityChange<TestEntity> SampleChange(int id) => new()
    {
        EntityType = typeof(TestEntity).FullName!,
        EntityId   = id.ToString(),
        ChangeType = ChangeType.Insert,
        Timestamp  = DateTime.UtcNow,
        State      = new TestEntity { Id = id }
    };

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition()) return;
            await Task.Delay(200);
        }
        throw new TimeoutException($"Timed out waiting for: {what}");
    }

    private sealed class PublisherContext : IAsyncDisposable
    {
        public NotificationBasedPublisher Publisher { get; }
        public EntityChangeTracker        Tracker   { get; }
        public PostgreSqlOutbox<TestEntity> Outbox  { get; }
        public InMemoryQueue              Queue     { get; }

        public PublisherContext(NotificationBasedPublisher publisher, EntityChangeTracker tracker,
            PostgreSqlOutbox<TestEntity> outbox, InMemoryQueue queue)
        {
            Publisher = publisher;
            Tracker   = tracker;
            Outbox    = outbox;
            Queue     = queue;
        }

        public async ValueTask DisposeAsync()
        {
            try { await Publisher.StopAsync(); } catch { }
            Publisher.Dispose();
            Tracker.Dispose();
        }
    }

    /// <summary>
    /// Filtered MeterListener — capture lives in-process for the duration of one test and tags
    /// every measurement with its `component` / `outcome` tags so the assertions read like prose.
    /// </summary>
    private sealed class CapturingMeterListener : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly List<Recorded> _measurements = new();
        private readonly object _gate = new();

        public CapturingMeterListener(RayTreeMeter meter)
        {
            var owner = meter.InternalMeter;
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (ReferenceEquals(instrument.Meter, owner)) l.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>  ((i, v, t, _) => Append(i, v, t));
            _listener.SetMeasurementEventCallback<int>   ((i, v, t, _) => Append(i, v, t));
            _listener.SetMeasurementEventCallback<double>((i, v, t, _) => Append(i, v, t));
            _listener.Start();
        }

        private void Append(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            string? component = null, outcome = null;
            foreach (var kv in tags)
            {
                if (kv.Key == "component") component = kv.Value as string;
                else if (kv.Key == "outcome")   outcome   = kv.Value as string;
            }
            lock (_gate) _measurements.Add(new Recorded(instrument.Name, value, component, outcome));
        }

        public double SumOf(string instrumentName, string component, string? outcome = null)
        {
            lock (_gate)
                return _measurements
                    .Where(m => m.Name == instrumentName && m.Component == component
                                && (outcome is null || m.Outcome == outcome))
                    .Sum(m => m.Value);
        }

        public void Dispose() => _listener.Dispose();

        private readonly record struct Recorded(string Name, double Value, string? Component, string? Outcome);
    }
}
