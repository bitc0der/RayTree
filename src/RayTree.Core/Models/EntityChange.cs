using RayTree.Tracking;

namespace RayTree.Models;

public class EntityChange
{
    public long Id { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public ChangeType ChangeType { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public int Version { get; set; } = 1;
    public Guid CorrelationId { get; set; } = Guid.NewGuid();
    public bool Published { get; set; }
}

public class EntityChange<TEntity> : EntityChange
{
    public TEntity? State { get; set; }

    public EntityChange()
    {
        State = default;
    }
}
