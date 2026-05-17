using RayTree.Core.Models;
using RayTree.Core.Tracking;

namespace RayTree.Core.Handling;

internal class HandlerRegistration
{
    public Type EntityType { get; set; } = null!;

    /// <summary>
    /// The change type this handler is registered for. Always a concrete value — the
    /// previous nullable "catch-all" form was removed. Each handler binds to exactly one
    /// of <see cref="Models.ChangeType.Insert"/> / <see cref="Models.ChangeType.Update"/>
    /// / <see cref="Models.ChangeType.Delete"/>; register multiple handlers if you need
    /// to react to several change types with the same logic.
    /// </summary>
    public ChangeType ChangeType { get; set; }

    /// <summary>
    /// Non-generic wrapper around the user-supplied <see cref="ChangeHandlerAsync{TEntity}"/>.
    /// The <see cref="EntityChange"/> passed here is always a typed <c>EntityChange&lt;TEntity&gt;</c>
    /// produced by deserialization, so the cast inside the wrapper is safe.
    /// </summary>
    public Func<EntityChange, CancellationToken, Task> Handler { get; set; } = null!;
}
