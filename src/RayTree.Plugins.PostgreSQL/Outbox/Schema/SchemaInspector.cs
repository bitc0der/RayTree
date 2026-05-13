using Npgsql;

namespace RayTree.Plugins.PostgreSQL.Schema;

public static class SchemaInspector
{
    public sealed record ExistingColumn(string Name, string NormalizedType, bool IsNullable);

    public static async Task<IReadOnlyDictionary<string, ExistingColumn>> GetColumnsAsync(
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
            var name = reader.GetString(0);
            var dataType = reader.GetString(1);
            var udtName = reader.GetString(2);
            var charMaxLength = reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3);
            var isNullable = reader.GetString(4) == "YES";

            var normalized = PostgreSqlTypeNormalizer.Normalize(dataType, udtName, charMaxLength);
            result[name] = new ExistingColumn(name, normalized, isNullable);
        }

        return result;
    }
}
