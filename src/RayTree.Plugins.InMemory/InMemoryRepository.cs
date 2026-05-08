using System.Collections.Concurrent;
using RayTree.Core.Plugins.Repository;

namespace RayTree.Plugins.InMemory;

public class InMemoryRepository<TEntity> : IRepository<TEntity> where TEntity : class
{
    private readonly ConcurrentDictionary<object, TEntity> _store = new();

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task InsertAsync(TEntity entity, CancellationToken cancellationToken = default)
    {
        var id = GetEntityId(entity);
        _store[id] = entity;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(TEntity entity, CancellationToken cancellationToken = default)
    {
        var id = GetEntityId(entity);
        _store[id] = entity;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(TEntity entity, CancellationToken cancellationToken = default)
    {
        var id = GetEntityId(entity);
        _store.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    public Task<TEntity?> GetByIdAsync(object id, CancellationToken cancellationToken = default)
    {
        _store.TryGetValue(id, out var entity);
        return Task.FromResult(entity);
    }

    public IReadOnlyDictionary<object, TEntity> GetAll() => _store;

    public void Clear() => _store.Clear();

    private static object GetEntityId(TEntity entity)
    {
        var prop = typeof(TEntity).GetProperty("Id")
                   ?? throw new InvalidOperationException($"Entity type {typeof(TEntity).Name} has no Id property");

        return prop.GetValue(entity)
               ?? throw new InvalidOperationException($"Entity Id is null");
    }
}
