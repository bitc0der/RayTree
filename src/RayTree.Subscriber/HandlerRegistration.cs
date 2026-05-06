using RayTree.Core.Tracking;

namespace RayTree.Subscriber;

internal class HandlerRegistration
{
    public Type EntityType { get; set; } = null!;
    public ChangeType? ChangeType { get; set; }
    public ChangeHandlerAsync Handler { get; set; } = null!;
}
