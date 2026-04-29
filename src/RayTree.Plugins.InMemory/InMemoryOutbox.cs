using System.Collections.Concurrent;
using RayTree.Models;
using RayTree.Tracking;

namespace RayTree.Plugins;

public class InMemoryOutbox : IOutbox
{
    private readonly ConcurrentBag<EntityChange> _entries = new();
    private readonly List<long> _publishedIds = new();
    private readonly object _lock = new();

    public Task WriteAsync(EntityChange change, CancellationToken cancellationToken = default)
    {
        change.Id = GenerateId();
        _entries.Add(change);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<EntityChange>> GetUnpublishedAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var unpublished = _entries
                .Where(e => !_publishedIds.Contains(e.Id))
                .OrderBy(e => e.Timestamp)
                .Take(batchSize)
                .ToList();

            return Task.FromResult<IReadOnlyList<EntityChange>>(unpublished);
        }
    }

    public Task<IReadOnlyList<EntityChange>> GetUnpublishedAsync(
        string entityType,
        ChangeType? changeType = null,
        DateTime? since = null,
        int batchSize = 100,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var query = _entries
                .Where(e => !_publishedIds.Contains(e.Id) && e.EntityType == entityType);

            if (changeType.HasValue)
                query = query.Where(e => e.ChangeType == changeType.Value);

            if (since.HasValue)
                query = query.Where(e => e.Timestamp >= since.Value);

            var unpublished = query
                .OrderBy(e => e.Timestamp)
                .Take(batchSize)
                .ToList();

            return Task.FromResult<IReadOnlyList<EntityChange>>(unpublished);
        }
    }

    public Task MarkPublishedAsync(long id, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_publishedIds.Contains(id))
                _publishedIds.Add(id);
        }

        return Task.CompletedTask;
    }

    public Task<int> CleanupPublishedAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow - retentionPeriod;
        var toRemove = new List<EntityChange>();

        lock (_lock)
        {
            foreach (var entry in _entries)
            {
                if (_publishedIds.Contains(entry.Id) && entry.Timestamp < cutoff)
                    toRemove.Add(entry);
            }
        }

        foreach (var entry in toRemove)
        {
            _entries.TryTake(out _);
        }

        return Task.FromResult(toRemove.Count);
    }

    public Task<EntityChange?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        var entry = _entries.FirstOrDefault(e => e.Id == id);
        return Task.FromResult<EntityChange?>(entry);
    }

    private static long _idCounter = 0;

    private static long GenerateId()
    {
        return Interlocked.Increment(ref _idCounter);
    }
}
