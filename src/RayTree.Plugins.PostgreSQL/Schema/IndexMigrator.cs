using Microsoft.Extensions.Logging;

namespace RayTree.Plugins.PostgreSQL.Schema;

internal sealed record IndexMigrationSpec(
    string Name,
    bool IsUnique,
    IReadOnlyList<string> Columns,
    string? Where);

internal static class IndexMigrator
{
    internal static async Task ApplyDiffAsync(
        string connectionString,
        string tableName,
        IReadOnlyList<IndexMigrationSpec> desired,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var actual = await SchemaInspector.GetIndexesAsync(connectionString, tableName, cancellationToken);
        var desiredByName = desired.ToDictionary(i => i.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var idx in desired)
        {
            if (!actual.TryGetValue(idx.Name, out var existing))
            {
                await SchemaInspector.ExecuteDdlAsync(connectionString, GenerateCreateIndex(tableName, idx), cancellationToken);
                logger.LogInformation("Created index {Index} on {Table}", idx.Name, tableName);
            }
            else if (!Matches(idx, existing))
            {
                await SchemaInspector.ExecuteDdlAsync(connectionString, $"DROP INDEX IF EXISTS public.{idx.Name};", cancellationToken);
                await SchemaInspector.ExecuteDdlAsync(connectionString, GenerateCreateIndex(tableName, idx), cancellationToken);
                logger.LogInformation("Recreated index {Index} on {Table} (definition changed)", idx.Name, tableName);
            }
        }

        foreach (var (name, _) in actual)
        {
            if (!desiredByName.ContainsKey(name))
                logger.LogWarning(
                    "Index '{Index}' exists on '{Table}' but is not in the entity schema — consider dropping it manually",
                    name, tableName);
        }
    }

    private static bool Matches(IndexMigrationSpec desired, SchemaInspector.ExistingIndex actual)
        => desired.IsUnique == actual.IsUnique
        && desired.Columns.SequenceEqual(actual.Columns, StringComparer.OrdinalIgnoreCase)
        && string.Equals(
            (desired.Where ?? string.Empty).Trim(),
            (actual.Where ?? string.Empty).Trim(),
            StringComparison.OrdinalIgnoreCase);

    private static string GenerateCreateIndex(string tableName, IndexMigrationSpec idx)
    {
        var unique = idx.IsUnique ? "UNIQUE " : "";
        var sql = $"CREATE {unique}INDEX IF NOT EXISTS {idx.Name} ON {tableName} ({string.Join(", ", idx.Columns)})";
        if (!string.IsNullOrEmpty(idx.Where))
            sql += $"\n    WHERE {idx.Where}";
        return sql + ";";
    }
}
