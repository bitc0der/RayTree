namespace RayTree.Plugins;

public interface IDdlExecutor
{
    Task ExecuteAsync(string ddl, CancellationToken cancellationToken = default);
    Task ExecuteFromFileAsync(string filePath, CancellationToken cancellationToken = default);
    Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken = default);
    Task<bool> TriggerExistsAsync(string triggerName, CancellationToken cancellationToken = default);
    Task<bool> FunctionExistsAsync(string functionName, CancellationToken cancellationToken = default);
}
