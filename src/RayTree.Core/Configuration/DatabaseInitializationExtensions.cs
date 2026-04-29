using RayTree.Outbox;
using RayTree.Plugins;
using RayTree.Tracking;

namespace RayTree.Configuration;

public class DatabaseInitializationOptions
{
    public bool CreateSourceTables { get; set; } = true;
    public bool CreateOutboxTables { get; set; } = true;
    public bool CreateTriggers { get; set; } = true;
    public bool CreateIndexes { get; set; } = true;
    public string NotificationChannel { get; set; } = "entity_changes";
    public string? SourceTableSuffix { get; set; } = "_source";
    public string? OutboxTableSuffix { get; set; } = "_outbox";
    public Func<Type, string, string>? TableNamingConvention { get; set; }
}

public static class DatabaseInitializationExtensions
{
    public static async Task InitializeDatabaseAsync(
        this EntityChangeTracker tracker,
        IDdlExecutor ddlExecutor,
        DatabaseInitializationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new DatabaseInitializationOptions();

        var schemas = BuildEntitySchemas(tracker, options);
        var generator = new CombinedDdlGenerator();

        var ddl = generator.GenerateInitializeAll(schemas, options.CreateTriggers, options.CreateIndexes);

        await ddlExecutor.ExecuteAsync(ddl, cancellationToken);
    }

    public static string GenerateInitializationDdl(
        this EntityChangeTracker tracker,
        DatabaseInitializationOptions? options = null)
    {
        options ??= new DatabaseInitializationOptions();

        var schemas = BuildEntitySchemas(tracker, options);
        var generator = new CombinedDdlGenerator();

        return generator.GenerateInitializeAll(schemas, options.CreateTriggers, options.CreateIndexes);
    }

    public static string GenerateDropDdl(
        this EntityChangeTracker tracker,
        DatabaseInitializationOptions? options = null)
    {
        options ??= new DatabaseInitializationOptions();

        var schemas = BuildEntitySchemas(tracker, options);
        var generator = new CombinedDdlGenerator();

        return generator.GenerateDropAll(schemas, options.CreateTriggers);
    }

    private static IEnumerable<(SourceTableSchema Source, OutboxTableSchema Outbox)> BuildEntitySchemas(
        EntityChangeTracker tracker,
        DatabaseInitializationOptions options)
    {
        foreach (var entityType in tracker.GetOutboxes().Keys)
        {
            var schemaName = entityType.Name.ToLowerInvariant();
            var sourceTableName = options.TableNamingConvention != null
                ? options.TableNamingConvention(entityType, schemaName)
                : $"{schemaName}{options.SourceTableSuffix}";

            var outboxTableName = $"{schemaName}{options.OutboxTableSuffix}";

            var sourceSchema = options.CreateSourceTables
                ? SourceTableDdlGenerator.CreateDefault(entityType.Name, sourceTableName)
                : new SourceTableSchema
                {
                    EntityTypeName = entityType.Name,
                    TableName = sourceTableName
                };

            var outboxSchema = options.CreateOutboxTables
                ? OutboxTableSchema.Create(entityType.Name, outboxTableName)
                : new OutboxTableSchema
                {
                    EntityTypeName = entityType.Name,
                    OutboxTableName = outboxTableName,
                    SourceTableName = sourceTableName
                };

            yield return (sourceSchema, outboxSchema);
        }
    }
}
