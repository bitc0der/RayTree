using RayTree.Plugins.PostgreSQL.Resilience;

namespace RayTree.Plugins.PostgreSQL.Outbox.Notification;

public class NotificationBasedPublisherOptions
{
    public string ConnectionString { get; set; } = string.Empty;
    public string ChannelName { get; set; } = "entity_changes";
    public TimeSpan FallbackPollingInterval { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Maximum number of NOTIFY events processed concurrently. Excess notifications
    /// are dropped and will be delivered by the fallback polling loop.</summary>
    public int MaxConcurrentNotifications { get; set; } = 16;
    /// <summary>
    /// Maximum number of changes published in parallel during fallback polling.
    /// Defaults to 1 (sequential) to preserve message ordering within a topic partition.
    /// Increase only when handlers are order-independent and throughput matters more than ordering.
    /// </summary>
    public int MaxPublishConcurrency { get; set; } = 1;
    /// <summary>
    /// Tunes the LISTEN-connection reconnect policy. The publisher detects a connection fault
    /// via <see cref="ListenLoopAsync"/>'s catch block, then runs an inline exponential-backoff
    /// loop bounded by these options. When <see cref="PostgresConnectionRecoveryOptions.Enabled"/> is
    /// <c>false</c>, the loop exits on the first failure and recovery falls entirely to the
    /// fallback polling path.
    /// </summary>
    public PostgresConnectionRecoveryOptions ConnectionRecovery { get; set; } = new();
}
