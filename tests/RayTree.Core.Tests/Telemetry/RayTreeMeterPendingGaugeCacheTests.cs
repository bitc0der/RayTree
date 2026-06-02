using RayTree.Core.Models;
using RayTree.Core.Plugins.Outbox;
using RayTree.Core.Telemetry;
using RayTree.Plugins.InMemory;

namespace RayTree.Core.Tests.Telemetry;

/// <summary>
/// The <c>raytree.outbox.pending</c> observable gauge calls <c>IOutbox.GetPendingCountAsync</c>
/// every collection tick. With sub-second OTel collection intervals (e.g. Prometheus scrape
/// every second) this would issue one DB query per outbox per tick. The meter caches results
/// for <see cref="RayTreeMeter.DefaultPendingCacheTtl"/> (10 s by default) to bound load.
/// These tests pin down that caching contract.
/// </summary>
[TestFixture]
public class RayTreeMeterPendingGaugeCacheTests
{
    private class Sample { public int Id { get; set; } }

    private sealed class CountingOutbox : IOutbox
    {
        private readonly InMemoryOutbox _inner = new();
        public int GetPendingCallCount;

        public Task InitializeAsync(CancellationToken ct = default) => _inner.InitializeAsync(ct);
        public Task WriteAsync<TEntity>(EntityChange<TEntity> change, CancellationToken ct = default) where TEntity : class
            => _inner.WriteAsync(change, ct);
        public Task<IReadOnlyList<EntityChange<TEntity>>> GetUnpublishedAsync<TEntity>(int batchSize, CancellationToken ct = default) where TEntity : class
            => _inner.GetUnpublishedAsync<TEntity>(batchSize, ct);
        public Task MarkPublishedAsync(long id, CancellationToken ct = default) => _inner.MarkPublishedAsync(id, ct);
        public Task<bool> TryClaimForPublishingAsync(long id, CancellationToken ct = default) => _inner.TryClaimForPublishingAsync(id, ct);
        public Task RevertClaimAsync(long id, CancellationToken ct = default) => _inner.RevertClaimAsync(id, ct);
        public Task<EntityChange<TEntity>?> GetByIdAsync<TEntity>(long id, CancellationToken ct = default) where TEntity : class
            => _inner.GetByIdAsync<TEntity>(id, ct);
        public Task<int> CleanupPublishedAsync(TimeSpan retentionPeriod, CancellationToken ct = default) => _inner.CleanupPublishedAsync(retentionPeriod, ct);
        public Task<int> CleanupStaleUnpublishedAsync(TimeSpan staleThreshold, CancellationToken ct = default) => _inner.CleanupStaleUnpublishedAsync(staleThreshold, ct);
        public Task<long> GetPendingCountAsync(Type entityType, CancellationToken ct = default)
        {
            Interlocked.Increment(ref GetPendingCallCount);
            return _inner.GetPendingCountAsync(entityType, ct);
        }
    }

    [Test]
    public void TwoObservationsWithinTtl_HitOutboxOnlyOnce()
    {
        // 10 s default TTL — two back-to-back samples must share a cache hit.
        using var meter = new RayTreeMeter();
        using var collector = new TestMetricsCollector(meter);

        var outbox = new CountingOutbox();
        meter.RegisterPendingGauge(() => new[] { (typeof(Sample), (IOutbox)outbox) });

        collector.RecordObservableInstruments();
        collector.RecordObservableInstruments();

        Assert.That(outbox.GetPendingCallCount, Is.EqualTo(1),
            "second observation within TTL must use the cached value");
    }

    [Test]
    public void TtlZero_EveryObservationHitsOutbox()
    {
        // TimeSpan.Zero disables caching — each sample must call the outbox.
        using var meter = new RayTreeMeter(pendingCacheTtl: TimeSpan.Zero);
        using var collector = new TestMetricsCollector(meter);

        var outbox = new CountingOutbox();
        meter.RegisterPendingGauge(() => new[] { (typeof(Sample), (IOutbox)outbox) });

        collector.RecordObservableInstruments();
        collector.RecordObservableInstruments();
        collector.RecordObservableInstruments();

        Assert.That(outbox.GetPendingCallCount, Is.EqualTo(3),
            "with TTL=0 every observation must poll the outbox");
    }

    [Test]
    public async Task TtlExpired_NextObservationRePollsOutbox()
    {
        // Short TTL with a generous expiry sleep — the margin (15× TTL) absorbs any
        // thread-pool or GC jitter on slow CI runners. Unit tests cannot inject a clock here
        // because RayTreeMeter currently reads DateTime.UtcNow directly; the wide ratio keeps
        // the test deterministic without that refactor.
        var ttl = TimeSpan.FromMilliseconds(20);
        using var meter = new RayTreeMeter(pendingCacheTtl: ttl);
        using var collector = new TestMetricsCollector(meter);

        var outbox = new CountingOutbox();
        meter.RegisterPendingGauge(() => new[] { (typeof(Sample), (IOutbox)outbox) });

        collector.RecordObservableInstruments();      // miss → 1 call
        collector.RecordObservableInstruments();      // hit  → still 1 call
        await Task.Delay(TimeSpan.FromMilliseconds(300));  // ≫ TTL, safe under CI jitter
        collector.RecordObservableInstruments();      // miss again → 2 calls

        Assert.That(outbox.GetPendingCallCount, Is.EqualTo(2));
    }
}
