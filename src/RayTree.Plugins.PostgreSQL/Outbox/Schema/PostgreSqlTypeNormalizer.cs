namespace RayTree.Plugins.PostgreSQL.Outbox.Schema;

public static class PostgreSqlTypeNormalizer
{
    private static readonly Dictionary<string, string> s_UdtElementTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["_int2"] = "SMALLINT",
        ["_int4"] = "INTEGER",
        ["_int8"] = "BIGINT",
        ["_float4"] = "REAL",
        ["_float8"] = "DOUBLE PRECISION",
        ["_numeric"] = "NUMERIC",
        ["_bool"] = "BOOLEAN",
        ["_uuid"] = "UUID",
        ["_text"] = "TEXT",
        ["_varchar"] = "TEXT",
        ["_timestamptz"] = "TIMESTAMPTZ",
        ["_timestamp"] = "TIMESTAMPTZ"
    };

    public static string Normalize(string dataType, string? udtName, int? charMaxLength)
    {
        if (string.Equals(dataType, "ARRAY", StringComparison.OrdinalIgnoreCase))
            return NormalizeArray(udtName);

        return dataType.ToLowerInvariant() switch
        {
            "smallint" or "int2" => "SMALLINT",
            "integer" or "int" or "int4" => "INTEGER",
            "bigint" or "int8" => "BIGINT",
            "real" or "float4" => "REAL",
            "double precision" or "float8" => "DOUBLE PRECISION",
            "numeric" or "decimal" => "NUMERIC",
            "boolean" or "bool" => "BOOLEAN",
            "uuid" => "UUID",
            "text" => "TEXT",
            "timestamp with time zone" or "timestamptz" => "TIMESTAMPTZ",
            "timestamp without time zone" or "timestamp" => "TIMESTAMP",
            "character varying" or "varchar" => charMaxLength.HasValue
                ? $"VARCHAR({charMaxLength.Value})"
                : "TEXT",
            _ => dataType.ToUpperInvariant()
        };
    }

    private static string NormalizeArray(string? udtName)
    {
        if (udtName is null)
            return "TEXT[]";

        if (s_UdtElementTypes.TryGetValue(udtName, out var elementType))
            return elementType + "[]";

        // Best-effort: strip leading underscore and uppercase
        return udtName.TrimStart('_').ToUpperInvariant() + "[]";
    }
}
