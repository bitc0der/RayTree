using Npgsql;

namespace RayTree.Plugins.PostgreSQL.Schema;

internal static class SchemaInspector
{
    internal sealed record ExistingColumn(string Name, string NormalizedType, bool IsNullable);
    internal sealed record ExistingIndex(string Name, bool IsUnique, IReadOnlyList<string> Columns, string? Where);

    internal static async Task<IReadOnlyDictionary<string, ExistingIndex>> GetIndexesAsync(
        string connectionString,
        string tableName,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, ExistingIndex>(StringComparer.OrdinalIgnoreCase);

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new NpgsqlCommand("""
            SELECT
                i.relname                                AS index_name,
                ix.indisunique                           AS is_unique,
                pg_get_expr(ix.indpred, ix.indrelid)     AS where_clause,
                ARRAY(
                    SELECT a.attname
                    FROM   unnest(ix.indkey::smallint[]) WITH ORDINALITY AS k(attnum, ord)
                    JOIN   pg_attribute a
                           ON  a.attrelid = ix.indrelid
                           AND a.attnum   = k.attnum
                    WHERE  k.attnum > 0
                    ORDER  BY k.ord
                )                                        AS columns
            FROM  pg_index    ix
            JOIN  pg_class    t  ON t.oid = ix.indrelid
            JOIN  pg_class    i  ON i.oid = ix.indexrelid
            JOIN  pg_namespace n ON n.oid = t.relnamespace
            WHERE n.nspname      = 'public'
              AND t.relname       = @TableName
              AND NOT ix.indisprimary
            """, conn);
        cmd.Parameters.Add(new NpgsqlParameter("TableName", tableName));

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name        = reader.GetString(0);
            var isUnique    = reader.GetBoolean(1);
            var whereClause = reader.IsDBNull(2) ? null : reader.GetString(2);
            var columns     = reader.GetFieldValue<string[]>(3);

            result[name] = new ExistingIndex(name, isUnique, columns, whereClause);
        }

        return result;
    }

    internal static async Task<bool> TableExistsAsync(
        string connectionString,
        string tableName,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand("""
            SELECT EXISTS(
                SELECT 1 FROM information_schema.tables
                WHERE table_schema = 'public' AND table_name = @TableName
            )
            """, conn);
        cmd.Parameters.Add(new NpgsqlParameter("TableName", tableName));
        return (bool)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }

    internal static async Task<IReadOnlyDictionary<string, ExistingColumn>> GetColumnsAsync(
        string connectionString,
        string tableName,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, ExistingColumn>(StringComparer.OrdinalIgnoreCase);

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new NpgsqlCommand("""
            SELECT column_name, data_type, udt_name, character_maximum_length, is_nullable
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @TableName
            ORDER BY ordinal_position
            """, conn);
        cmd.Parameters.Add(new NpgsqlParameter("TableName", tableName));

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name          = reader.GetString(0);
            var dataType      = reader.GetString(1);
            var udtName       = reader.GetString(2);
            var charMaxLength = reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3);
            var isNullable    = reader.GetString(4) == "YES";

            var normalized = PostgreSqlTypeNormalizer.Normalize(dataType, udtName, charMaxLength);
            result[name] = new ExistingColumn(name, normalized, isNullable);
        }

        return result;
    }

    internal static async Task ExecuteDdlAsync(
        string connectionString,
        string ddl,
        CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(ddl, conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static async Task<bool> TableHasRowsAsync(
        string connectionString,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand($"SELECT EXISTS(SELECT 1 FROM {tableName} LIMIT 1)", conn);
        return (bool)(await cmd.ExecuteScalarAsync(cancellationToken))!;
    }
}
