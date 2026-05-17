using RayTree.Core.Tracking;

namespace RayTree.Hosting;

public class EntityChangeTrackerFactory
{
    public EntityChangeTracker Create(Action<IChangeTrackingBuilder>? configure = null)
    {
        var builder = EntityChangeTracker.Create();
        configure?.Invoke(builder);
        return builder.Build();
    }

    public async Task<EntityChangeTracker> CreateAsync(Action<IChangeTrackingBuilder>? configure = null,
        CancellationToken cancellationToken = default)
    {
        var builder = EntityChangeTracker.Create();
        configure?.Invoke(builder);
        return await builder.BuildAsync(cancellationToken);
    }
}
