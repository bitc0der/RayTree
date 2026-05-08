using RayTree.Core.Models;
using RayTree.Core.Tracking;

namespace RayTree.Core.Handling;

internal class HandlerRegistration
{
    public Type EntityType { get; set; } = null!;
    public ChangeType? ChangeType { get; set; }

    /// <summary>
    /// Non-generic wrapper around the user-supplied <see cref="ChangeHandlerAsync{TEntity}"/>.
    /// The <see cref="EntityChange"/> passed here is always a typed <c>EntityChange&lt;TEntity&gt;</c>
    /// produced by deserialization, so the cast inside the wrapper is safe.
    /// </summary>
    public Func<EntityChange, CancellationToken, Task> Handler { get; set; } = null!;
}
