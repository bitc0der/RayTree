using RayTree.Core.Plugins.Outbox;

namespace RayTree.Core.Distribution;

public sealed class OutboxCleanupService
{
    private readonly IEnumerable<IOutbox> _outboxes;
    private readonly TimeSpan _retentionPeriod;

    public OutboxCleanupService(IEnumerable<IOutbox> outboxes, TimeSpan? retentionPeriod = null)
    {
        _outboxes = outboxes ?? throw new ArgumentNullException(nameof(outboxes));
        _retentionPeriod = retentionPeriod ?? TimeSpan.FromDays(7);
    }

    public async Task<int> RunCleanupAsync(CancellationToken cancellationToken = default)
    {
        var totalDeleted = 0;

        foreach (var outbox in _outboxes)
        {
            var deleted = await outbox.CleanupPublishedAsync(_retentionPeriod, cancellationToken);
            totalDeleted += deleted;
        }

        return totalDeleted;
    }
}
