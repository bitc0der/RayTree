using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RayTree.Core.Models;
using RayTree.Core.Plugins.Outbox;
using RayTree.Core.Tracking;

namespace RayTree.EntityFrameworkCore.Interceptors;

public class EntityChangeInterceptor : SaveChangesInterceptor
{
    private readonly EntityChangeTracker _tracker;
    private readonly IEnumerable<Type> _trackedEntityTypes;

    private static readonly MethodInfo WriteTypedMethod = typeof(EntityChangeInterceptor)
        .GetMethod(nameof(WriteTypedAsyncCore), BindingFlags.NonPublic | BindingFlags.Static)!;

    // CreateChange used to do MakeGenericType + Activator.CreateInstance + reflective
    // property SetValue on every changed entity, every SaveChanges call. Compiling a
    // factory delegate once per entity type (via Expression.Lambda, cached here) turns
    // that into a cheap delegate invocation on the hot path.
    private static readonly ConcurrentDictionary<Type, Func<object, EntityChange>> ChangeFactories = new();

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
        var factory = ChangeFactories.GetOrAdd(entityType, CompileChangeFactory);
        var change = factory(entry.Entity);
        change.EntityType = entityType.AssemblyQualifiedName ?? entityType.FullName ?? entityType.Name;
        change.EntityId = entry.Property("Id").CurrentValue?.ToString() ?? Guid.NewGuid().ToString();
        change.ChangeType = changeType;
        change.Timestamp = DateTime.UtcNow;
        return change;
    }

    // Builds `entity => (EntityChange)new EntityChange<TEntity> { State = (TEntity)entity }`
    // once per entity type, compiled to IL instead of interpreted via MakeGenericType/SetValue.
    private static Func<object, EntityChange> CompileChangeFactory(Type entityType)
    {
        var genericChangeType = typeof(EntityChange<>).MakeGenericType(entityType);
        var stateProperty = genericChangeType.GetProperty(nameof(EntityChange<object>.State))!;

        var entityParam = Expression.Parameter(typeof(object), "entity");
        var changeVar = Expression.Variable(genericChangeType, "change");
        var body = Expression.Block(
            [changeVar],
            Expression.Assign(changeVar, Expression.New(genericChangeType)),
            Expression.Call(changeVar, stateProperty.SetMethod!, Expression.Convert(entityParam, entityType)),
            Expression.Convert(changeVar, typeof(EntityChange)));

        return Expression.Lambda<Func<object, EntityChange>>(body, entityParam).Compile();
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

            var outbox = _tracker.GetOutbox(entityType);

            await WriteTypedAsync(outbox, change, entityType, cancellationToken);
        }

        ChangeContext.Clear(dbContext);
    }

    private static Task WriteTypedAsync(IOutbox outbox, EntityChange change, Type entityType, CancellationToken ct)
        => (Task)WriteTypedMethod.MakeGenericMethod(entityType).Invoke(null, [outbox, change, ct])!;

    private static Task WriteTypedAsyncCore<TEntity>(
        IOutbox outbox,
        EntityChange<TEntity> change,
        CancellationToken ct)
        where TEntity : class
    {
        return outbox.WriteAsync(change, ct);
    }
}
