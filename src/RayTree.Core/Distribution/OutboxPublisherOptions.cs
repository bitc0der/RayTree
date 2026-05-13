namespace RayTree.Core.Distribution;

public class OutboxPublisherOptions
{
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(5);
    public int BatchSize { get; set; } = 100;
    public bool UseNotificationChannel { get; set; }
    public string? NotificationChannel { get; set; }
    public TimeSpan? FallbackPollingInterval { get; set; }
    public int MaxRetryCount { get; set; } = 3;
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan CleanupRetentionPeriod { get; set; } = TimeSpan.FromDays(7);
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan? StaleUnpublishedThreshold { get; set; }
    /// <summary>
    /// Maximum number of changes within a batch that are published in parallel.
    /// Defaults to 1 (sequential) to preserve message ordering within a topic partition.
    /// Increase only when handlers are order-independent and throughput matters more than ordering.
    /// </summary>
    public int MaxPublishConcurrency { get; set; } = 1;
}
