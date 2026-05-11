using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Reflection;
using RayTree.Core.Plugins.Repository;

namespace RayTree.Plugins.InMemory;

public class InMemoryRepository<TEntity> : IRepository<TEntity> where TEntity : class
{
    private readonly ConcurrentDictionary<string, TEntity> _store = new();
    private readonly IReadOnlyList<PropertyInfo> _keyProperties;

    public InMemoryRepository()
    {
        _keyProperties = ResolveKeyProperties();
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task InsertAsync(TEntity entity, CancellationToken cancellationToken = default)
    {
        _store[BuildKey(entity)] = entity;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(TEntity entity, CancellationToken cancellationToken = default)
    {
        _store[BuildKey(entity)] = entity;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(TEntity entity, CancellationToken cancellationToken = default)
    {
        _store.TryRemove(BuildKey(entity), out _);
        return Task.CompletedTask;
    }

    public Task<TEntity?> GetByIdAsync(object[] keyValues, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyValues);

        if (keyValues.Length != _keyProperties.Count)
            throw new ArgumentException(
                $"Expected {_keyProperties.Count} key value(s) for {typeof(TEntity).Name}, got {keyValues.Length}.",
                nameof(keyValues));

        var key = string.Join('\0', keyValues.Select(v => v?.ToString() ?? string.Empty));
        _store.TryGetValue(key, out var entity);
        return Task.FromResult(entity);
    }

    public IReadOnlyDictionary<string, TEntity> GetAll() => _store;

    public void Clear() => _store.Clear();

    private string BuildKey(TEntity entity)
        => string.Join('\0', _keyProperties.Select(p => p.GetValue(entity)?.ToString() ?? string.Empty));

    // Key resolution mirrors EntityColumnMapper.GetKeyProperties in RayTree.Plugins.PostgreSQL.
    // InMemory has no dependency on that package, so the logic is intentionally co-located here.
    // If the resolution rules change, update both places.
    private static IReadOnlyList<PropertyInfo> ResolveKeyProperties()
    {
        var props = typeof(TEntity).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var keyed = props
            .Where(p => p.CanRead && p.CanWrite && p.IsDefined(typeof(KeyAttribute), inherit: true))
            .OrderBy(p =>
            {
                var order = p.GetCustomAttribute<ColumnAttribute>(inherit: true)?.Order ?? -1;
                return order >= 0 ? order : int.MaxValue;
            })
            .ThenBy(p => Array.IndexOf(props, p))
            .ToList();

        if (keyed.Count > 0)
            return keyed;

        var idProp = props.FirstOrDefault(p => p.Name == "Id" && p.CanRead && p.CanWrite);
        if (idProp != null)
            return [idProp];

        throw new InvalidOperationException(
            $"Entity type '{typeof(TEntity).Name}' has no [Key]-annotated property and no 'Id' convention property.");
    }
}
