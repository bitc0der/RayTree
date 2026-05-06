namespace RayTree.Distribution;

public class NotificationPayload
{
    public string EntityType { get; set; } = string.Empty;
    public long Id { get; set; }
    public string ChangeType { get; set; } = string.Empty;
}
