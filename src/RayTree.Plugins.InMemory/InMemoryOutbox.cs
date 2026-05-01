using System.Collections.Concurrent;
using RayTree.Models;
using RayTree.Tracking;

namespace RayTree.Plugins.InMemory;

public class InMemoryOutbox : IOutbox
{
    private readonly ConcurrentDictionary<long, EntityChange> _store = new();
    private long _nextId;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task WriteAsync(EntityChange change, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        change.Id = id;
        _store[id] = change;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<EntityChange>> GetUnpublishedAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        var changes = _store.Values
            .Where(c => !c.Published)
            .OrderBy(c => c.Timestamp)
            .Take(batchSize)
            .ToList();

        return Task.FromResult<IReadOnlyList<EntityChange>>(changes);
    }

    public Task<IReadOnlyList<EntityChange>> GetUnpublishedAsync(
        string entityType,
        ChangeType? changeType = null,
        DateTime? since = null,
        int batchSize = 100,
        CancellationToken cancellationToken = default)
    {
        var changes = _store.Values
            .Where(c => !c.Published && c.EntityType == entityType)
            .Where(c => !changeType.HasValue || c.ChangeType == changeType.Value)
            .Where(c => !since.HasValue || c.Timestamp >= since.Value)
            .OrderBy(c => c.Timestamp)
            .Take(batchSize)
            .ToList();

        return Task.FromResult<IReadOnlyList<EntityChange>>(changes);
    }

    public Task MarkPublishedAsync(long id, CancellationToken cancellationToken = default)
    {
        if (_store.TryGetValue(id, out var change))
        {
            change.Published = true;
        }
        return Task.CompletedTask;
    }

    public Task<int> CleanupPublishedAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow - retentionPeriod;
        var toRemove = _store.Values
            .Where(c => c.Published && c.Timestamp < cutoff)
            .Select(c => c.Id)
            .ToList();

        foreach (var id in toRemove)
        {
            _store.TryRemove(id, out _);
        }

        return Task.FromResult(toRemove.Count);
    }

    public Task<EntityChange?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        _store.TryGetValue(id, out var change);
        return Task.FromResult(change);
    }

    public IReadOnlyList<EntityChange> GetAll() => _store.Values.ToList();

    public void Clear()
    {
        _store.Clear();
        _nextId = 0;
    }
}
