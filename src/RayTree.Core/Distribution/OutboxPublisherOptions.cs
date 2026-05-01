namespace RayTree.Distribution;

public class OutboxPublisherOptions
{
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(5);
    public int BatchSize { get; set; } = 100;
    public bool UseNotificationChannel { get; set; }
    public string? NotificationChannel { get; set; }
    public TimeSpan? FallbackPollingInterval { get; set; }
    public int MaxRetryCount { get; set; } = 3;
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);
}
