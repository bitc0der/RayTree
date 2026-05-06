using RayTree.Core.Tracking;

namespace RayTree.Core.Models;

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

/// <summary>
/// Captures a change to an entity (insert, update, or delete) with the full typed entity state.
/// <list type="bullet">
///   <item>For inserts and updates, <see cref="State"/> holds the entity state after the operation.</item>
///   <item>For deletes, <see cref="State"/> holds the entity state before deletion.</item>
/// </list>
/// </summary>
/// <typeparam name="TEntity">The entity type whose state is being tracked.</typeparam>
public class EntityChange<TEntity> : EntityChange
    where TEntity : class
{
    public TEntity? State { get; set; }
}
