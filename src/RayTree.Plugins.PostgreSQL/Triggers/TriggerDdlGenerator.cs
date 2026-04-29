namespace RayTree.Plugins.PostgreSQL;

public class TriggerDdlGenerator
{
    public static string GenerateSourceTableTrigger(string sourceTableName, string outboxTableName, string triggerName = "trg_entity_change_outbox")
    {
        return $"""
            CREATE OR REPLACE FUNCTION fn_{triggerName}()
            RETURNS TRIGGER AS $$
            BEGIN
                INSERT INTO {outboxTableName} (
                    entity_id,
                    change_type,
                    timestamp,
                    version,
                    correlation_id,
                    entity_type,
                    published
                ) VALUES (
                    COALESCE(NEW.id, gen_random_uuid())::text,
                    CASE TG_OP
                        WHEN 'INSERT' THEN 'Insert'
                        WHEN 'UPDATE' THEN 'Update'
                        WHEN 'DELETE' THEN 'Delete'
                    END,
                    NOW(),
                    COALESCE(NEW.version, 1),
                    gen_random_uuid(),
                    '{sourceTableName}',
                    FALSE
                );

                RETURN CASE TG_OP
                    WHEN 'DELETE' THEN OLD
                    ELSE NEW
                END;
            END;
            $$ LANGUAGE plpgsql;

            CREATE TRIGGER {triggerName}
                AFTER INSERT OR UPDATE OR DELETE ON {sourceTableName}
                FOR EACH ROW EXECUTE FUNCTION fn_{triggerName}();
            """;
    }

    public static string GenerateNotifyTrigger(string outboxTableName, string channelName, string triggerName = "trg_outbox_notify")
    {
        return $"""
            CREATE OR REPLACE FUNCTION fn_{triggerName}()
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
                AFTER INSERT ON {outboxTableName}
                FOR EACH ROW EXECUTE FUNCTION fn_{triggerName}();
            """;
    }

    public static string GenerateDropTriggers(string sourceTableName, string outboxTableName, string sourceTriggerName = "trg_entity_change_outbox", string notifyTriggerName = "trg_outbox_notify")
    {
        return $"""
            DROP TRIGGER IF EXISTS {sourceTriggerName} ON {sourceTableName};
            DROP FUNCTION IF EXISTS fn_{sourceTriggerName}();
            DROP TRIGGER IF EXISTS {notifyTriggerName} ON {outboxTableName};
            DROP FUNCTION IF EXISTS fn_{notifyTriggerName}();
            """;
    }

    public static string GenerateInstallAll(string sourceTableName, string outboxTableName, string channelName)
    {
        return GenerateSourceTableTrigger(sourceTableName, outboxTableName) + "\n\n" +
               GenerateNotifyTrigger(outboxTableName, channelName);
    }
}
