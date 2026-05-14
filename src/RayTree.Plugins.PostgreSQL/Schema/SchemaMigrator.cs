using Microsoft.Extensions.Logging;
using Npgsql;

namespace RayTree.Plugins.PostgreSQL.Schema;

internal sealed record ColumnMigrationSpec(string Name, string Type, bool IsNullable);

internal static class SchemaMigrator
{
    internal static async Task ApplyDiffAsync(
        string connectionString,
        string tableName,
        IReadOnlyList<ColumnMigrationSpec> desired,
        Func<ColumnMigrationSpec, string> generateAddColumn,
        Func<string, bool> isOrphanCandidate,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var actual = await SchemaInspector.GetColumnsAsync(connectionString, tableName, cancellationToken);
        var desiredByName = desired.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        bool? tableHasRows = null;

        foreach (var col in desired)
        {
            if (actual.ContainsKey(col.Name))
                continue;

            if (!col.IsNullable)
            {
                tableHasRows ??= await TableHasRowsAsync(connectionString, tableName, cancellationToken);
                if (tableHasRows.Value)
                    throw new InvalidOperationException(
                        $"Cannot add column '{col.Name}': it is NOT NULL with no default and table " +
                        $"'{tableName}' already has rows. Add a DEFAULT or migrate manually.");
            }

            await ExecuteDdlAsync(connectionString, generateAddColumn(col), cancellationToken);
            logger.LogInformation("Auto-migrated: added column {Column} ({Type}) to {Table}",
                col.Name, col.Type, tableName);
        }

        foreach (var (name, _) in actual)
        {
            if (!isOrphanCandidate(name)) continue;
            if (!desiredByName.ContainsKey(name))
                logger.LogWarning(
                    "Column '{Column}' exists in '{Table}' but has no matching entity property — consider dropping it manually",
                    name, tableName);
        }

        foreach (var col in desired)
        {
            if (!actual.TryGetValue(col.Name, out var existing)) continue;
            if (!string.Equals(existing.NormalizedType, col.Type, StringComparison.OrdinalIgnoreCase))
                logger.LogWarning(
                    "Column '{Column}' in '{Table}' has type '{Actual}' but entity expects '{Expected}' — type changes must be migrated manually",
                    col.Name, tableName, existing.NormalizedType, col.Type);
        }
    }

    private static async Task<bool> TableHasRowsAsync(string connectionString, string tableName,
        CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand($"SELECT EXISTS(SELECT 1 FROM {tableName} LIMIT 1)", conn);
        return (bool)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task ExecuteDdlAsync(string connectionString, string ddl,
        CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(ddl, conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
