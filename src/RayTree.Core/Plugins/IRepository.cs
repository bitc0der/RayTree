namespace RayTree.Plugins;

public interface IRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

public interface IRepository<TEntity> : IRepository where TEntity : class
{
    Task InsertAsync(TEntity entity, CancellationToken cancellationToken = default);
    Task UpdateAsync(TEntity entity, CancellationToken cancellationToken = default);
    Task DeleteAsync(TEntity entity, CancellationToken cancellationToken = default);
    Task<TEntity?> GetByIdAsync(object id, CancellationToken cancellationToken = default);
}
