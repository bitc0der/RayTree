using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RayTree.Models;
using RayTree.Tracking;

namespace RayTree.EntityFrameworkCore.Interceptors;

public class EntityChangeInterceptor : SaveChangesInterceptor
{
    private readonly EntityChangeTracker _tracker;
    private readonly IEnumerable<Type> _trackedEntityTypes;

    public EntityChangeInterceptor(EntityChangeTracker tracker, IEnumerable<Type> trackedEntityTypes)
    {
        _tracker = tracker;
        _trackedEntityTypes = trackedEntityTypes;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        var dbContext = eventData.Context;
        if (dbContext == null)
            return base.SavingChangesAsync(eventData, result, cancellationToken);

        var changes = CaptureChanges(dbContext);
        if (changes.Count > 0)
        {
            ChangeContext.Set(dbContext, changes);
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        var dbContext = eventData.Context;
        if (dbContext == null)
            return base.SavingChanges(eventData, result);

        var changes = CaptureChanges(dbContext);
        if (changes.Count > 0)
        {
            ChangeContext.Set(dbContext, changes);
        }

        return base.SavingChanges(eventData, result);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        await WriteOutboxAsync(eventData.Context, cancellationToken);
        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(
        SaveChangesCompletedEventData eventData,
        int result)
    {
        WriteOutboxAsync(eventData.Context, CancellationToken.None).GetAwaiter().GetResult();
        return base.SavedChanges(eventData, result);
    }

    private List<EntityChange> CaptureChanges(DbContext dbContext)
    {
        var changes = new List<EntityChange>();

        foreach (var entry in dbContext.ChangeTracker.Entries())
        {
            var entityType = entry.Entity.GetType();
            if (!_trackedEntityTypes.Contains(entityType))
                continue;

            var change = entry.State switch
            {
                EntityState.Added => CreateChange(entry, entityType, ChangeType.Insert),
                EntityState.Modified => CreateChange(entry, entityType, ChangeType.Update),
                EntityState.Deleted => CreateChange(entry, entityType, ChangeType.Delete),
                _ => null
            };

            if (change != null)
                changes.Add(change);
        }

        return changes;
    }

    private static EntityChange CreateChange(EntityEntry entry, Type entityType, ChangeType changeType)
    {
        return new EntityChange
        {
            EntityType = entityType.AssemblyQualifiedName ?? entityType.FullName ?? entityType.Name,
            EntityId = entry.Property("Id").CurrentValue?.ToString() ?? Guid.NewGuid().ToString(),
            ChangeType = changeType,
            Timestamp = DateTime.UtcNow
        };
    }

    private async Task WriteOutboxAsync(DbContext? dbContext, CancellationToken cancellationToken)
    {
        if (dbContext == null)
            return;

        var changes = ChangeContext.Get(dbContext);
        if (changes.Count == 0)
            return;

        foreach (var change in changes)
        {
            var entityType = Type.GetType(change.EntityType);
            if (entityType == null)
                continue;

            var outbox = _tracker.GetOutboxes().GetValueOrDefault(entityType);
            if (outbox != null)
            {
                await outbox.WriteAsync(change, cancellationToken);
            }
        }

        ChangeContext.Clear(dbContext);
    }
}
