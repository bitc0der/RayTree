using Microsoft.Extensions.Logging;
using RayTree.Core.Plugins.Outbox;

namespace RayTree.Core.Distribution;

public sealed class OutboxCleanupService
{
    private readonly IEnumerable<IOutbox> _outboxes;
    private readonly TimeSpan _retentionPeriod;
    private readonly ILogger<OutboxCleanupService> _logger;

    public OutboxCleanupService(
        IEnumerable<IOutbox> outboxes,
        ILogger<OutboxCleanupService> logger,
        TimeSpan? retentionPeriod = null)
    {
        _outboxes        = outboxes ?? throw new ArgumentNullException(nameof(outboxes));
        _logger          = logger   ?? throw new ArgumentNullException(nameof(logger));
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

        _logger.LogInformation("Outbox cleanup complete: deleted {TotalDeleted} record(s) older than {RetentionPeriod}",
            totalDeleted, _retentionPeriod);

        return totalDeleted;
    }
}
