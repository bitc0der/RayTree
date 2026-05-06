namespace RayTree.Plugins.PostgreSQL.Outbox.Notification;

public class NotificationBasedPublisherOptions
{
    public string ConnectionString { get; set; } = string.Empty;
    public string ChannelName { get; set; } = "entity_changes";
    public TimeSpan FallbackPollingInterval { get; set; } = TimeSpan.FromSeconds(30);
}
