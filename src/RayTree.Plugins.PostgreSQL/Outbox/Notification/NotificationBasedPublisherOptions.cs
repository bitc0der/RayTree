namespace RayTree.Plugins.PostgreSQL.Outbox.Notification;

public class NotificationBasedPublisherOptions
{
    public string ConnectionString { get; set; } = string.Empty;
    public string ChannelName { get; set; } = "entity_changes";
    public TimeSpan FallbackPollingInterval { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Maximum number of NOTIFY events processed concurrently. Excess notifications
    /// are dropped and will be delivered by the fallback polling loop.</summary>
    public int MaxConcurrentNotifications { get; set; } = 16;
    /// <summary>Maximum number of changes published in parallel during fallback polling.</summary>
    public int MaxPublishConcurrency { get; set; } = Environment.ProcessorCount;
}
