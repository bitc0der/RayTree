namespace RayTree.Plugins.PostgreSQL.Outbox.Schema;

public class OutboxTableSchema
{
    public string EntityTypeName { get; set; } = string.Empty;
    public string OutboxTableName { get; set; } = string.Empty;
    public string SourceTableName { get; set; } = string.Empty;
    public List<OutboxColumn> Columns { get; set; } = new();
    public List<OutboxIndex> Indexes { get; set; } = new();
    public List<EntityPropertyColumn> EntityPropertyColumns { get; set; } = new();

    public static OutboxTableSchema Create(string entityTypeName, string? outboxTableOverride = null)
    {
        var schemaName = entityTypeName.ToLowerInvariant();
        return new OutboxTableSchema
        {
            EntityTypeName = entityTypeName,
            OutboxTableName = outboxTableOverride ?? $"{schemaName}_outbox",
            SourceTableName = $"{schemaName}_source",
            Columns =
            [
                new OutboxColumn { Name = "id", Type = "BIGSERIAL", IsPrimaryKey = true },
                new OutboxColumn { Name = "entity_id", Type = "TEXT", IsNullable = false },
                new OutboxColumn { Name = "change_type", Type = "VARCHAR(10)", IsNullable = false },
                new OutboxColumn { Name = "timestamp", Type = "TIMESTAMPTZ", IsNullable = false, Default = "NOW()" },
                new OutboxColumn { Name = "published", Type = "BOOLEAN", IsNullable = false, Default = "FALSE" },
                new OutboxColumn { Name = "version", Type = "INTEGER", IsNullable = false, Default = "1" },
                new OutboxColumn
                {
                    Name = "correlation_id", Type = "UUID", IsNullable = false, Default = "gen_random_uuid()"
                },
                new OutboxColumn
                {
                    Name = "entity_type", Type = "TEXT", IsNullable = false, Default = $"'{entityTypeName}'"
                }
            ],
            Indexes =
            [
                new OutboxIndex
                {
                    Name = $"idx_{schemaName}_outbox_unpublished",
                    Columns = ["published", "timestamp"],
                    Where = "published = FALSE"
                },
                new OutboxIndex
                {
                    Name = $"idx_{schemaName}_outbox_entity", Columns = ["entity_type", "published", "timestamp"]
                }
            ]
        };
    }

    public void AddEntityPropertyColumn(string propertyName, string columnName, string columnType,
        bool isNullable = true)
    {
        EntityPropertyColumns.Add(new EntityPropertyColumn
        {
            PropertyName = propertyName, ColumnName = columnName, ColumnType = columnType, IsNullable = isNullable
        });

        Columns.Add(new OutboxColumn { Name = columnName, Type = columnType, IsNullable = isNullable });
    }
}
