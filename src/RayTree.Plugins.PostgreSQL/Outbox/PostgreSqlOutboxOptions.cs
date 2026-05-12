namespace RayTree.Plugins.PostgreSQL.Outbox;

public class PostgreSqlOutboxOptions
{
    public string ConnectionString { get; set; } = string.Empty;
    public string OutboxTableName { get; set; } = string.Empty;
    public bool UseNotificationChannel { get; set; }
    public string? NotificationChannel { get; set; }
    public TimeSpan? FallbackPollingInterval { get; set; }
    public int CleanupBatchSize { get; set; } = 1000;
}
