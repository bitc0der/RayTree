using System.Text;

namespace RayTree.Plugins.PostgreSQL.Repository.Schema;

public static class SourceTableDdlGenerator
{
    public static string GenerateCreateTable(SourceTableSchema schema, bool ifNotExists = true)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"CREATE TABLE {(ifNotExists ? "IF NOT EXISTS " : "")}{schema.TableName} (");

        for (var i = 0; i < schema.Columns.Count; i++)
        {
            var col = schema.Columns[i];
            sb.Append($"    {col.Name} {col.Type}");

            if (col.IsIdentity)
                sb.Append(" GENERATED ALWAYS AS IDENTITY");

            if (col.IsPrimaryKey)
                sb.Append(" PRIMARY KEY");

            if (!col.IsPrimaryKey && !col.IsNullable && !col.IsIdentity)
                sb.Append(" NOT NULL");

            if (col.Default != null)
                sb.Append($" DEFAULT {col.Default}");

            if (i < schema.Columns.Count - 1)
                sb.AppendLine(",");
            else
                sb.AppendLine();
        }

        sb.AppendLine(");");

        foreach (var index in schema.Indexes)
        {
            sb.AppendLine();
            sb.AppendLine(GenerateCreateIndex(schema.TableName, index));
        }

        return sb.ToString();
    }

    public static string GenerateCreateIndex(string tableName, SourceTableIndex index)
    {
        var unique = index.IsUnique ? "UNIQUE " : "";
        var sql =
            $"CREATE {unique}INDEX IF NOT EXISTS {index.Name} ON {tableName} ({string.Join(", ", index.Columns)})";

        if (!string.IsNullOrEmpty(index.Where))
            sql += $"\n    WHERE {index.Where}";

        sql += ";";
        return sql;
    }

    public static SourceTableSchema CreateDefault(
        string entityTypeName,
        IReadOnlyList<SourceTableColumn> keyColumns,
        string? tableNameOverride = null)
    {
        var schemaName = entityTypeName.ToLowerInvariant();

        var columns = new List<SourceTableColumn>
        {
            new() { Name = "id", Type = "BIGINT", IsPrimaryKey = true, IsIdentity = true },
            new() { Name = "created_at", Type = "TIMESTAMPTZ", IsNullable = false, Default = "NOW()" },
            new() { Name = "updated_at", Type = "TIMESTAMPTZ", IsNullable = false, Default = "NOW()" },
            new() { Name = "version", Type = "INTEGER", IsNullable = false, Default = "1" }
        };
        columns.AddRange(keyColumns);

        var indexes = new List<SourceTableIndex>
        {
            new() { Name = $"idx_{schemaName}_source_created", Columns = ["created_at"] }
        };
        if (keyColumns.Count > 0)
            indexes.Add(new SourceTableIndex
            {
                Name = $"idx_{schemaName}_source_key",
                Columns = keyColumns.Select(c => c.Name).ToList(),
                IsUnique = true
            });

        return new SourceTableSchema
        {
            EntityTypeName = entityTypeName,
            TableName = tableNameOverride ?? $"{schemaName}_source",
            Columns = columns,
            Indexes = indexes
        };
    }
}
