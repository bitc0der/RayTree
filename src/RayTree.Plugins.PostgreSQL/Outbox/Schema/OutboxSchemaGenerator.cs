using System.Text;

namespace RayTree.Plugins.PostgreSQL.Outbox.Schema;

public static class OutboxSchemaGenerator
{
    public static string GenerateCreateTable(OutboxTableSchema schema, bool includeIndexes = true)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"CREATE TABLE IF NOT EXISTS {schema.OutboxTableName} (");

        for (var i = 0; i < schema.Columns.Count; i++)
        {
            var col = schema.Columns[i];
            sb.Append($"    {col.Name} {col.Type}");

            if (col.IsPrimaryKey)
                sb.Append(" PRIMARY KEY");

            if (!col.IsNullable && !col.IsPrimaryKey)
                sb.Append(" NOT NULL");

            if (col.Default != null)
                sb.Append($" DEFAULT {col.Default}");

            if (i < schema.Columns.Count - 1)
                sb.AppendLine(",");
            else
                sb.AppendLine();
        }

        sb.AppendLine(");");

        if (includeIndexes)
        {
            foreach (var index in schema.Indexes)
            {
                sb.AppendLine();
                sb.Append(
                    $"CREATE INDEX IF NOT EXISTS {index.Name} ON {schema.OutboxTableName} ({string.Join(", ", index.Columns)})");

                if (!string.IsNullOrEmpty(index.Where))
                    sb.AppendLine($"\n    WHERE {index.Where};");
                else
                    sb.AppendLine(";");
            }
        }

        return sb.ToString();
    }

    public static string GenerateAddColumn(string tableName, OutboxColumn col)
    {
        var sb = new StringBuilder($"ALTER TABLE {tableName} ADD COLUMN IF NOT EXISTS {col.Name} {col.Type}");
        if (!col.IsNullable && !col.IsPrimaryKey)
            sb.Append(" NOT NULL");
        if (col.Default != null)
            sb.Append($" DEFAULT {col.Default}");
        sb.Append(';');
        return sb.ToString();
    }
}
