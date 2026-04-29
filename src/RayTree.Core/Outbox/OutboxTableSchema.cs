using RayTree.Models;

namespace RayTree.Outbox;

public class OutboxTableSchema
{
    public string EntityTypeName { get; set; } = string.Empty;
    public string OutboxTableName { get; set; } = string.Empty;
    public string SourceTableName { get; set; } = string.Empty;
    public List<OutboxColumn> Columns { get; set; } = new();
    public List<OutboxIndex> Indexes { get; set; } = new();

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
                new OutboxColumn { Name = "entity_id", Type = "UUID", IsNullable = false },
                new OutboxColumn { Name = "change_type", Type = "VARCHAR(10)", IsNullable = false },
                new OutboxColumn { Name = "timestamp", Type = "TIMESTAMPTZ", IsNullable = false, Default = "NOW()" },
                new OutboxColumn { Name = "published", Type = "BOOLEAN", IsNullable = false, Default = "FALSE" },
                new OutboxColumn { Name = "version", Type = "INTEGER", IsNullable = false, Default = "1" },
                new OutboxColumn { Name = "correlation_id", Type = "UUID", IsNullable = false, Default = "gen_random_uuid()" },
                new OutboxColumn { Name = "entity_type", Type = "VARCHAR(100)", IsNullable = false, Default = $"'{entityTypeName}'" },
                new OutboxColumn { Name = "data", Type = "JSONB", IsNullable = true }
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
                    Name = $"idx_{schemaName}_outbox_entity",
                    Columns = ["entity_type", "published", "timestamp"]
                }
            ]
        };
    }
}

public class OutboxColumn
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool IsPrimaryKey { get; set; }
    public bool IsNullable { get; set; }
    public string? Default { get; set; }
}

public class OutboxIndex
{
    public string Name { get; set; } = string.Empty;
    public List<string> Columns { get; set; } = new();
    public string? Where { get; set; }
}
