using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RayTree.Core.Distribution;
using RayTree.Core.Models;
using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Compression;
using RayTree.Core.Plugins.Outbox;
using RayTree.Core.Plugins.Publisher;
using RayTree.Core.Plugins.Serialization;
using RayTree.Core.Telemetry;
using RayTree.Core.Tracking;
using RayTree.Core.Tests.Telemetry;
using RayTree.Plugins.InMemory;
using RayTree.Plugins.Serializers.Json;

namespace RayTree.Core.Tests.Resilience;

/// <summary>
/// Covers the outbox connection-fault observability hooks added to
/// <see cref="OutboxPublisherService"/> — disconnect/recovery metric emission, per-transition
/// gating, and log-level demotion when <see cref="IOutbox.IsConnectionFault"/> classifies
/// the batch failure as a connection-level fault.
/// </summary>
[TestFixture]
public class OutboxPublisherServiceConnectionFaultTests
{
    private class Sample { public int Id { get; set; } }

    /// <summary>
    /// Hand-rolled <see cref="IOutbox"/> stub: throws on every <c>GetUnpublishedAsync</c>
    /// until <see cref="HealAfter"/> calls, then succeeds with an empty batch. The classifier
    /// returns the value of <see cref="IsFault"/>; <see cref="Component"/> drives the
    /// observability path.
    /// </summary>
    private sealed class StubOutbox : IOutbox
    {
        public Func<Exception> ExceptionFactory { get; set; } = () => new InvalidOperationException("stub fault");
        public bool IsFault { get; set; } = true;
        public string? Component { get; set; } = "postgres.outbox";
        public string? Endpoint  { get; set; } = "host:5432";
        public int HealAfter     { get; set; } = int.MaxValue;
        public int CallCount     { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task WriteAsync<TEntity>(EntityChange<TEntity> change, CancellationToken cancellationToken = default) where TEntity : class => Task.CompletedTask;

        public Task<IReadOnlyList<EntityChange<TEntity>>> GetUnpublishedAsync<TEntity>(int batchSize, CancellationToken cancellationToken = default) where TEntity : class
        {
            CallCount++;
            if (CallCount <= HealAfter)
                throw ExceptionFactory();
            return Task.FromResult<IReadOnlyList<EntityChange<TEntity>>>(Array.Empty<EntityChange<TEntity>>());
        }

        public Task<IReadOnlyList<EntityChange<TEntity>>> GetUnpublishedAsync<TEntity>(ChangeType? changeType = null, DateTime? since = null, int batchSize = 100, CancellationToken cancellationToken = default) where TEntity : class
            => Task.FromResult<IReadOnlyList<EntityChange<TEntity>>>(Array.Empty<EntityChange<TEntity>>());

        public Task MarkPublishedAsync(long id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> TryClaimForPublishingAsync(long id, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task RevertClaimAsync(long id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<int> CleanupPublishedAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<int> CleanupStaleUnpublishedAsync(TimeSpan staleThreshold, CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<long> GetPendingCountAsync(Type entityType, CancellationToken cancellationToken = default) => Task.FromResult(0L);
        public Task<EntityChange<TEntity>?> GetByIdAsync<TEntity>(long id, CancellationToken cancellationToken = default) where TEntity : class
            => Task.FromResult<EntityChange<TEntity>?>(null);

        public bool IsConnectionFault(Exception ex)  => IsFault;
        public string? ConnectionComponent           => Component;
        public string? ConnectionEndpoint            => Endpoint;
    }

    /// <summary>
    /// Captures log entries so tests can assert level + structured properties.
    /// </summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<(LogLevel Level, string Category, string Message, Exception? Exception)> Entries { get; } = new();
        private readonly object _gate = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);
        public void Dispose() { }

        public bool Contains(LogLevel level, string substring)
        {
            lock (_gate)
                return Entries.Any(e => e.Level == level && e.Message.Contains(substring, StringComparison.OrdinalIgnoreCase));
        }

        public int Count(LogLevel level)
        {
            lock (_gate)
                return Entries.Count(e => e.Level == level);
        }

        private sealed class CapturingLogger : ILogger
        {
            private readonly CapturingLoggerProvider _owner;
            private readonly string _category;

            public CapturingLogger(CapturingLoggerProvider owner, string category)
            {
                _owner = owner;
                _category = category;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (_owner._gate)
                    _owner.Entries.Add((logLevel, _category, formatter(state, exception), exception));
            }
        }
    }

    private static (ChangePublisher publisher, RayTreeMeter meter, TestMetricsCollector collector, CapturingLoggerProvider logs, ILoggerFactory factory)
        Build(StubOutbox outbox)
    {
        var meter = new RayTreeMeter();
        var collector = new TestMetricsCollector(meter);
        var logs = new CapturingLoggerProvider();
        var factory = LoggerFactory.Create(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Debug));
        var publisher = new ChangePublisher(factory, meter);
        publisher.RegisterOutbox(typeof(Sample), outbox);
        publisher.RegisterPublisher(typeof(Sample), new InMemoryQueue());
        publisher.RegisterSerializer(typeof(Sample), new JsonSerializerPlugin());
        publisher.RegisterCompressor(typeof(Sample), new NoOpCompressorPlugin());
        return (publisher, meter, collector, logs, factory);
    }

    private static OutboxPublisherOptions FastPolling => new()
    {
        BatchSize             = 10,
        PollingInterval       = TimeSpan.FromMilliseconds(20),
        MaxPublishConcurrency = 1,
        MaxRetryCount         = 1
    };

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        throw new TimeoutException("Condition not met within timeout");
    }

    [Test]
    public async Task ConnectionFault_FirstFailure_EmitsDisconnect_AndLogsWarningNotError()
    {
        var outbox = new StubOutbox { IsFault = true, Component = "postgres.outbox", Endpoint = "host:5432" };
        var (publisher, meter, collector, logs, factory) = Build(outbox);
        using var _ = meter; using var __ = collector; using var ___ = publisher; using var ____ = factory;

        using var svc = new OutboxPublisherService(publisher, typeof(Sample), FastPolling, factory, meter);
        await svc.StartAsync();
        await WaitForAsync(() => collector.Sum("raytree.connection.disconnects") >= 1, TimeSpan.FromSeconds(3));
        await svc.StopAsync();

        Assert.That(collector.Sum("raytree.connection.disconnects"), Is.EqualTo(1),
            "Disconnect counter should fire exactly once across multiple failed batches");
        var disconnect = collector.Get("raytree.connection.disconnects")[0];
        Assert.That(disconnect.Tags["component"], Is.EqualTo("postgres.outbox"));
        Assert.That(disconnect.Tags["endpoint"],  Is.EqualTo("host:5432"));

        Assert.That(logs.Contains(LogLevel.Warning, "Outbox connection fault"), Is.True);
        // The Error log path for batch errors must NOT fire on connection faults.
        Assert.That(logs.Contains(LogLevel.Error, "Error processing outbox batch"), Is.False,
            "Connection-fault path must demote Error → Warning");
    }

    [Test]
    public async Task ConnectionFault_RecoversOnNextSuccessfulBatch_EmitsRecoveryMetric_AndInfoLog()
    {
        var outbox = new StubOutbox { IsFault = true, Component = "postgres.outbox", Endpoint = "h:5432", HealAfter = 2 };
        var (publisher, meter, collector, logs, factory) = Build(outbox);
        using var _ = meter; using var __ = collector; using var ___ = publisher; using var ____ = factory;

        using var svc = new OutboxPublisherService(publisher, typeof(Sample), FastPolling, factory, meter);
        await svc.StartAsync();
        await WaitForAsync(() =>
            collector.Sum("raytree.connection.recoveries") >= 1, TimeSpan.FromSeconds(5));
        await svc.StopAsync();

        var recovery = collector.Get("raytree.connection.recoveries")[0];
        Assert.That(recovery.Tags["component"], Is.EqualTo("postgres.outbox"));
        Assert.That(recovery.Tags["outcome"],   Is.EqualTo("succeeded"));
        Assert.That(collector.Get("raytree.connection.recovery.duration"), Has.Count.GreaterThanOrEqualTo(1));
        Assert.That(logs.Contains(LogLevel.Information, "Outbox connection recovered"), Is.True);
    }

    [Test]
    public async Task NonConnectionFault_EmitsNoMetric_AndPreservesErrorLog()
    {
        // IsFault = false: outbox classifies the exception as non-connection. The existing
        // Error log path SHALL be preserved and no connection metric SHALL be emitted.
        var outbox = new StubOutbox { IsFault = false, Component = "postgres.outbox" };
        var (publisher, meter, collector, logs, factory) = Build(outbox);
        using var _ = meter; using var __ = collector; using var ___ = publisher; using var ____ = factory;

        using var svc = new OutboxPublisherService(publisher, typeof(Sample), FastPolling, factory, meter);
        await svc.StartAsync();
        await WaitForAsync(() => logs.Count(LogLevel.Error) >= 1, TimeSpan.FromSeconds(3));
        await svc.StopAsync();

        Assert.That(collector.Sum("raytree.connection.disconnects"), Is.EqualTo(0),
            "Non-fault exceptions must not emit the connection-disconnect metric");
        Assert.That(logs.Contains(LogLevel.Error, "Error processing outbox batch"), Is.True,
            "Non-fault exceptions must preserve the Error log path");
    }

    [Test]
    public async Task ConnectionComponentNull_FallsThroughToErrorPath_NoMetric()
    {
        // Even when IsFault = true, a null ConnectionComponent means the outbox declines
        // to participate in connection observability. Fall through to the Error path,
        // no metric emission.
        var outbox = new StubOutbox { IsFault = true, Component = null };
        var (publisher, meter, collector, logs, factory) = Build(outbox);
        using var _ = meter; using var __ = collector; using var ___ = publisher; using var ____ = factory;

        using var svc = new OutboxPublisherService(publisher, typeof(Sample), FastPolling, factory, meter);
        await svc.StartAsync();
        await WaitForAsync(() => logs.Count(LogLevel.Error) >= 1, TimeSpan.FromSeconds(3));
        await svc.StopAsync();

        Assert.That(collector.Sum("raytree.connection.disconnects"), Is.EqualTo(0));
        Assert.That(logs.Contains(LogLevel.Error, "Error processing outbox batch"), Is.True);
    }
}
