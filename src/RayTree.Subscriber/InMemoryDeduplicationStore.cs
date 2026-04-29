using System.Collections.Concurrent;

namespace RayTree.Subscriber;

public class InMemoryDeduplicationStore : IDeduplicationStore
{
    private readonly ConcurrentDictionary<string, DateTime> _processed = new();

    public Task<bool> TryMarkProcessedAsync(string correlationId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_processed.TryAdd(correlationId, DateTime.UtcNow));
    }

    public Task<bool> IsProcessedAsync(string correlationId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_processed.ContainsKey(correlationId));
    }

    public Task CleanupAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow - retentionPeriod;
        var toRemove = _processed.Where(kvp => kvp.Value < cutoff).Select(kvp => kvp.Key).ToList();

        foreach (var key in toRemove)
        {
            _processed.TryRemove(key, out _);
        }

        return Task.CompletedTask;
    }
}
