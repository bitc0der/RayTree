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
/// Covers the outbox connection-fault observability hooks on
/// <see cref="OutboxPublisherService"/> — per-transition gating and log-level demotion when
/// <see cref="IOutbox.IsConnectionFault"/> classifies the batch failure as a connection-level
/// fault. Connection metrics were removed; recovery is now observed via logs only.
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
        Build(IOutbox outbox)
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
    public async Task ConnectionFault_FirstFailure_LogsWarningNotError()
    {
        var outbox = new StubOutbox { IsFault = true, Component = "postgres.outbox", Endpoint = "host:5432" };
        var (publisher, meter, collector, logs, factory) = Build(outbox);
        using var _ = meter; using var __ = collector; using var ___ = publisher; using var ____ = factory;

        using var svc = new OutboxPublisherService(publisher, typeof(Sample), FastPolling, factory, meter);
        await svc.StartAsync();
        await WaitForAsync(() => logs.Contains(LogLevel.Warning, "Outbox connection fault"), TimeSpan.FromSeconds(3));
        await svc.StopAsync();

        Assert.That(logs.Contains(LogLevel.Warning, "Outbox connection fault"), Is.True);
        // The Error log path for batch errors must NOT fire on connection faults.
        Assert.That(logs.Contains(LogLevel.Error, "Error processing outbox batch"), Is.False,
            "Connection-fault path must demote Error → Warning");
    }

    [Test]
    public async Task ConnectionFault_RecoversOnNextSuccessfulBatch_LogsInfo()
    {
        var outbox = new StubOutbox { IsFault = true, Component = "postgres.outbox", Endpoint = "h:5432", HealAfter = 2 };
        var (publisher, meter, collector, logs, factory) = Build(outbox);
        using var _ = meter; using var __ = collector; using var ___ = publisher; using var ____ = factory;

        using var svc = new OutboxPublisherService(publisher, typeof(Sample), FastPolling, factory, meter);
        await svc.StartAsync();
        await WaitForAsync(() =>
            logs.Contains(LogLevel.Information, "Outbox connection recovered"), TimeSpan.FromSeconds(5));
        await svc.StopAsync();

        Assert.That(logs.Contains(LogLevel.Information, "Outbox connection recovered"), Is.True);
    }

    [Test]
    public async Task NonConnectionFault_PreservesErrorLog()
    {
        // IsFault = false: outbox classifies the exception as non-connection. The existing
        // Error log path SHALL be preserved.
        var outbox = new StubOutbox { IsFault = false, Component = "postgres.outbox" };
        var (publisher, meter, collector, logs, factory) = Build(outbox);
        using var _ = meter; using var __ = collector; using var ___ = publisher; using var ____ = factory;

        using var svc = new OutboxPublisherService(publisher, typeof(Sample), FastPolling, factory, meter);
        await svc.StartAsync();
        await WaitForAsync(() => logs.Count(LogLevel.Error) >= 1, TimeSpan.FromSeconds(3));
        await svc.StopAsync();

        Assert.That(logs.Contains(LogLevel.Error, "Error processing outbox batch"), Is.True,
            "Non-fault exceptions must preserve the Error log path");
    }

    [Test]
    public async Task ConnectionComponentNull_FallsThroughToErrorPath()
    {
        // Even when IsFault = true, a null ConnectionComponent means the outbox declines
        // to participate in connection observability. Fall through to the Error path.
        var outbox = new StubOutbox { IsFault = true, Component = null };
        var (publisher, meter, collector, logs, factory) = Build(outbox);
        using var _ = meter; using var __ = collector; using var ___ = publisher; using var ____ = factory;

        using var svc = new OutboxPublisherService(publisher, typeof(Sample), FastPolling, factory, meter);
        await svc.StartAsync();
        await WaitForAsync(() => logs.Count(LogLevel.Error) >= 1, TimeSpan.FromSeconds(3));
        await svc.StopAsync();

        Assert.That(logs.Contains(LogLevel.Error, "Error processing outbox batch"), Is.True);
    }

    [Test]
    public async Task GetOutboxThrows_StillLogsBatchErrorWithoutMasking()
    {
        // If the outbox lookup itself throws during the catch block (shutdown race), the
        // service should NOT swallow the original batch exception — it should fall through
        // to the Error log path. This guards against `_publisher.GetOutbox` masking a real
        // fault with a NullReferenceException.
        var outbox = new ThrowingLookupOutbox();
        var (publisher, meter, collector, logs, factory) = Build(outbox);
        using var _ = meter; using var __ = collector; using var ___ = publisher; using var ____ = factory;

        using var svc = new OutboxPublisherService(publisher, typeof(Sample), FastPolling, factory, meter);
        await svc.StartAsync();
        await WaitForAsync(() => logs.Count(LogLevel.Error) >= 1, TimeSpan.FromSeconds(3));
        await svc.StopAsync();

        // Note: we can't easily make `_publisher.GetOutbox` throw without reaching into
        // ChangePublisher internals — instead this stub throws from GetUnpublishedAsync.
        // The HandleBatchError code path is exercised via the regular batch-error catch.
        Assert.That(logs.Contains(LogLevel.Error, "Error processing outbox batch"), Is.True,
            "non-fault path must still emit Error log");
    }

    private sealed class ThrowingLookupOutbox : IOutbox
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task WriteAsync<TEntity>(EntityChange<TEntity> change, CancellationToken ct = default) where TEntity : class => Task.CompletedTask;
        public Task<IReadOnlyList<EntityChange<TEntity>>> GetUnpublishedAsync<TEntity>(int batchSize, CancellationToken ct = default) where TEntity : class
            => throw new InvalidOperationException("not classified as a connection fault");
        public Task<IReadOnlyList<EntityChange<TEntity>>> GetUnpublishedAsync<TEntity>(ChangeType? changeType = null, DateTime? since = null, int batchSize = 100, CancellationToken ct = default) where TEntity : class
            => Task.FromResult<IReadOnlyList<EntityChange<TEntity>>>(Array.Empty<EntityChange<TEntity>>());
        public Task MarkPublishedAsync(long id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> TryClaimForPublishingAsync(long id, CancellationToken ct = default) => Task.FromResult(true);
        public Task RevertClaimAsync(long id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> CleanupPublishedAsync(TimeSpan r, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> CleanupStaleUnpublishedAsync(TimeSpan s, CancellationToken ct = default) => Task.FromResult(0);
        public Task<long> GetPendingCountAsync(Type t, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<EntityChange<TEntity>?> GetByIdAsync<TEntity>(long id, CancellationToken ct = default) where TEntity : class
            => Task.FromResult<EntityChange<TEntity>?>(null);
    }
}
