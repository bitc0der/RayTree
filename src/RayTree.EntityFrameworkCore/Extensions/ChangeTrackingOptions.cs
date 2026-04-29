namespace RayTree.EntityFrameworkCore.Extensions;

public class ChangeTrackingOptions
{
    public List<Type> TrackedEntityTypes { get; } = new();
    public bool AutoAttachInterceptor { get; set; } = true;
    public List<Type> ExcludedDbContexts { get; } = new();

    public ChangeTrackingOptions TrackEntity<T>()
    {
        TrackedEntityTypes.Add(typeof(T));
        return this;
    }

    public ChangeTrackingOptions ExcludeDbContext<T>()
    {
        ExcludedDbContexts.Add(typeof(T));
        return this;
    }
}
