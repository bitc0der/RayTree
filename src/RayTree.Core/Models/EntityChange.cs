using RayTree.Tracking;

namespace RayTree.Models;

/// <summary>
/// Captures metadata about a change to an entity (insert, update, or delete).
/// This non-generic form is used for backward compatibility and low-level pipeline operations.
/// For typed entity state, use <see cref="EntityChange{TEntity}"/>.
/// </summary>
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
/// Extends <see cref="EntityChange"/> with the full typed entity state captured at the time of the change.
/// <list type="bullet">
///   <item>For inserts and updates, <see cref="State"/> holds the entity state after the operation.</item>
///   <item>For deletes, <see cref="State"/> holds the entity state before deletion.</item>
/// </list>
/// <example>
/// <code>
/// var change = await tracker.TrackInsertAsync(newProduct);
/// // change.State is the Product that was inserted
/// Console.WriteLine(change.State.Name);
/// </code>
/// </example>
/// </summary>
/// <typeparam name="TEntity">The entity type whose state is being tracked.</typeparam>
public class EntityChange<TEntity> : EntityChange
{
    /// <summary>The typed entity state at the time of the change. Defaults to <c>default(TEntity)</c>.</summary>
    public TEntity? State { get; set; }

    public EntityChange()
    {
        State = default;
    }
}
