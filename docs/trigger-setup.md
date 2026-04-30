# Database Trigger Setup Guide

RayTree supports PostgreSQL `NOTIFY/LISTEN` for near-instant outbox change detection, as an alternative to (or alongside) periodic polling.

## How It Works

1. A trigger on the outbox table fires on `INSERT`
2. The trigger calls `pg_notify()` with the channel name and payload
3. The `NotificationBasedPublisher` receives the notification and publishes the change
4. If the LISTEN connection drops, fallback polling activates automatically

## Enable Notification Mode

```csharp
tracking.ForEntity<Product>()
    .UsePostgreSqlOutbox(connectionString, "products", config =>
    {
        config.UseNotificationChannel("entity_changes");
        config.WithFallbackPolling(TimeSpan.FromSeconds(30));
    });
```

## Trigger DDL

### Generate Trigger Script

```csharp
var tracker = serviceProvider.GetRequiredService<IEntityChangeTracker>();
var triggerDdl = tracker.GenerateNotifyTriggerDdl<Product>("products_outbox");
Console.WriteLine(triggerDdl);
```

### Manual Trigger Creation

```sql
-- Create the notify function (once per database)
CREATE OR REPLACE FUNCTION notify_outbox_change()
RETURNS TRIGGER AS $$
BEGIN
    PERFORM pg_notify(
        'entity_changes',
        json_build_object(
            'entity_type', NEW.entity_type,
            'entity_id', NEW.entity_id,
            'change_type', NEW.change_type,
            'outbox_id', NEW.id
        )::text
    );
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Create trigger on each outbox table
CREATE TRIGGER products_outbox_notify_trigger
    AFTER INSERT ON products_outbox
    FOR EACH ROW
    EXECUTE FUNCTION notify_outbox_change();
```

### Drop Trigger

```sql
DROP TRIGGER IF EXISTS products_outbox_notify_trigger ON products_outbox;
```

## Configuration Options

### Channel Name

```csharp
config.UseNotificationChannel("my_custom_channel");
```

Default: `"entity_changes"`

Multiple publishers can listen on different channels for entity-type-specific routing.

### Fallback Polling

```csharp
config.WithFallbackPolling(
    pollingInterval: TimeSpan.FromSeconds(30),
    batchSize: 50);
```

Fallback polling activates when:
- LISTEN connection is lost
- Reconnection fails
- No notifications received within the polling interval

### Reconnection Behavior

The `NotificationBasedPublisher` automatically:
1. Attempts to reconnect with exponential backoff (1s, 2s, 4s, 8s, max 30s)
2. On successful reconnect, scans the outbox table for missed entries
3. Publishes any unpublished entries found during the gap
4. Resumes LISTEN on the configured channel

## Monitoring

### Check Trigger Status

```sql
SELECT trigger_name, event_object_table
FROM information_schema.triggers
WHERE trigger_name LIKE '%notify%';
```

### Check LISTEN Channels

```sql
SELECT channel, payload
FROM pg_notification_queue;
```

### Verify Notifications

```sql
LISTEN entity_changes;
NOTIFY entity_changes, '{"test": true}';
-- Check pgAdmin or psql output for notification
```

## Troubleshooting

### Not Receiving Notifications

1. Verify the trigger exists:
   ```sql
   \d products_outbox
   ```
2. Verify the `pg_notify` function exists:
   ```sql
   \df notify_outbox_change
   ```
3. Check PostgreSQL logs for trigger errors

### High Polling Fallback Frequency

If fallback polling activates too often:
- Check network stability between app and PostgreSQL
- Increase `WithFallbackPolling()` interval
- Check PostgreSQL `max_connections` and connection pool settings

### Missed Changes After Reconnect

The publisher scans for unpublished entries on reconnect. Verify:
- Outbox entries are not being deleted before publishing
- `is_published` column is correctly set after publish
- The notification channel name matches between trigger config and publisher config
