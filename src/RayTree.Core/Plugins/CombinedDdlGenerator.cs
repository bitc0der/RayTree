using System.Text;
using RayTree.Outbox;

namespace RayTree.Plugins;

public class CombinedDdlGenerator
{
    public string GenerateInitialize(
        SourceTableSchema sourceSchema,
        OutboxTableSchema outboxSchema,
        bool includeTriggers = true,
        bool includeIndexes = true)
    {
        var sb = new StringBuilder();

        sb.AppendLine("-- =============================================");
        sb.AppendLine($"-- Initialize: {sourceSchema.EntityTypeName}");
        sb.AppendLine("-- =============================================");
        sb.AppendLine();

        sb.AppendLine("-- 1. Source table");
        sb.AppendLine(SourceTableDdlGenerator.GenerateCreateTable(sourceSchema, ifNotExists: true));
        sb.AppendLine();

        sb.AppendLine("-- 2. Outbox table");
        sb.AppendLine(OutboxSchemaGenerator.GenerateCreateTable(outboxSchema, includeIndexes));
        sb.AppendLine();

        if (includeTriggers)
        {
            sb.AppendLine("-- 3. Source table -> Outbox trigger");
            sb.AppendLine(GenerateSourceToOutboxTrigger(sourceSchema, outboxSchema));
            sb.AppendLine();

            sb.AppendLine("-- 4. Outbox NOTIFY trigger");
            sb.AppendLine(GenerateOutboxNotifyTrigger(outboxSchema, "entity_changes"));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public string GenerateInitializeAll(
        IEnumerable<(SourceTableSchema Source, OutboxTableSchema Outbox)> entitySchemas,
        bool includeTriggers = true,
        bool includeIndexes = true)
    {
        var sb = new StringBuilder();

        sb.AppendLine("-- =============================================");
        sb.AppendLine("-- RayTree Entity Change Tracking - Full Initialization");
        sb.AppendLine($"-- Generated: {DateTime.UtcNow:O}");
        sb.AppendLine("-- =============================================");
        sb.AppendLine();

        foreach (var (source, outbox) in entitySchemas)
        {
            sb.AppendLine(GenerateInitialize(source, outbox, includeTriggers, includeIndexes));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public string GenerateDropAll(
        IEnumerable<(SourceTableSchema Source, OutboxTableSchema Outbox)> entitySchemas,
        bool includeTriggers = true)
    {
        var sb = new StringBuilder();

        sb.AppendLine("-- =============================================");
        sb.AppendLine("-- RayTree Entity Change Tracking - Drop All");
        sb.AppendLine("-- =============================================");
        sb.AppendLine();

        foreach (var (source, outbox) in entitySchemas)
        {
            sb.AppendLine($"-- Drop triggers for {source.EntityTypeName}");
            if (includeTriggers)
            {
                sb.AppendLine($"DROP TRIGGER IF EXISTS trg_{source.TableName.ToLowerInvariant()}_outbox ON {source.TableName};");
                sb.AppendLine($"DROP FUNCTION IF EXISTS fn_{source.TableName.ToLowerInvariant()}_outbox();");
                sb.AppendLine($"DROP TRIGGER IF EXISTS trg_{outbox.OutboxTableName.ToLowerInvariant()}_notify ON {outbox.OutboxTableName};");
                sb.AppendLine($"DROP FUNCTION IF EXISTS fn_{outbox.OutboxTableName.ToLowerInvariant()}_notify();");
            }
            sb.AppendLine();

            sb.AppendLine($"-- Drop outbox table for {source.EntityTypeName}");
            sb.AppendLine(OutboxSchemaGenerator.GenerateDropTable(outbox));
            sb.AppendLine();

            sb.AppendLine($"-- Drop source table for {source.EntityTypeName}");
            sb.AppendLine(SourceTableDdlGenerator.GenerateDropTable(source.TableName));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string GenerateSourceToOutboxTrigger(SourceTableSchema source, OutboxTableSchema outbox)
    {
        var triggerName = $"trg_{source.TableName.ToLowerInvariant()}_outbox";
        var functionName = $"fn_{source.TableName.ToLowerInvariant()}_outbox";

        return $"""
            CREATE OR REPLACE FUNCTION {functionName}()
            RETURNS TRIGGER AS $$
            BEGIN
                INSERT INTO {outbox.OutboxTableName} (
                    entity_id,
                    change_type,
                    timestamp,
                    version,
                    correlation_id,
                    entity_type,
                    published
                ) VALUES (
                    COALESCE(NEW.id::text, gen_random_uuid()::text),
                    CASE TG_OP
                        WHEN 'INSERT' THEN 'Insert'
                        WHEN 'UPDATE' THEN 'Update'
                        WHEN 'DELETE' THEN 'Delete'
                    END,
                    NOW(),
                    COALESCE(NEW.version, 1),
                    gen_random_uuid(),
                    '{source.EntityTypeName}',
                    FALSE
                );

                RETURN CASE TG_OP
                    WHEN 'DELETE' THEN OLD
                    ELSE NEW
                END;
            END;
            $$ LANGUAGE plpgsql;

            CREATE TRIGGER {triggerName}
                AFTER INSERT OR UPDATE OR DELETE ON {source.TableName}
                FOR EACH ROW EXECUTE FUNCTION {functionName}();
            """;
    }

    private static string GenerateOutboxNotifyTrigger(OutboxTableSchema outbox, string channelName)
    {
        var triggerName = $"trg_{outbox.OutboxTableName.ToLowerInvariant()}_notify";
        var functionName = $"fn_{outbox.OutboxTableName.ToLowerInvariant()}_notify";

        return $"""
            CREATE OR REPLACE FUNCTION {functionName}()
            RETURNS TRIGGER AS $$
            BEGIN
                PERFORM pg_notify('{channelName}', json_build_object(
                    'entity_type', NEW.entity_type,
                    'id', NEW.id,
                    'change_type', NEW.change_type
                )::text);
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;

            CREATE TRIGGER {triggerName}
                AFTER INSERT ON {outbox.OutboxTableName}
                FOR EACH ROW EXECUTE FUNCTION {functionName}();
            """;
    }
}
