# PostgreSQL NOTIFY/LISTEN Setup

RayTree supports PostgreSQL `NOTIFY/LISTEN` for near-instant outbox change detection alongside the default periodic fallback polling.

## How It Works

1. A trigger on the outbox table fires `pg_notify()` on every `INSERT`
2. `NotificationBasedPublisher` receives the notification and publishes the change immediately
3. A fallback polling loop runs independently — it catches any changes missed if the LISTEN connection drops

## Configuration

### Step 1 — Enable the trigger on the outbox

Set `UseNotificationChannel = true` (and optionally the channel name) when creating the outbox:

```csharp
// Directly — outbox table name is derived from the entity DTO ([Table] attribute or snake_case convention)
var outbox = new PostgreSqlOutbox<Product>(new PostgreSqlOutboxOptions
{
    ConnectionString = connectionString,
    UseNotificationChannel = true,
    NotificationChannel = "products_notify"   // defaults to "entity_changes"
});

// Or using the extension methods
var options = new PostgreSqlOutboxOptions
{
    ConnectionString = connectionString
};
options.UseNotificationChannel("products_notify")
       .WithFallbackPolling(TimeSpan.FromSeconds(30));
```

When `UseNotificationChannel = true`, calling `Build()` / `BuildAsync()` automatically creates the trigger function and attaches the trigger to the outbox table — no manual SQL required.

### Step 2 — Create and start NotificationBasedPublisher

`NotificationBasedPublisher` is a standalone component that holds its own persistent LISTEN connection:

```csharp
var publisher = new NotificationBasedPublisher(
    tracker,
    new NotificationBasedPublisherOptions
    {
        ConnectionString = connectionString,
        ChannelName = "products_notify",         // must match the outbox channel name
        FallbackPollingInterval = TimeSpan.FromSeconds(30)
    },
    loggerFactory);  // ILoggerFactory — required

await publisher.StartAsync();

// On shutdown:
await publisher.StopAsync();
publisher.Dispose();
```

`tracker` is the `EntityChangeTracker` that has the outbox, queue publisher, serializer, and compressor registered for each entity type. `NotificationBasedPublisher` uses it to resolve these per-entity dependencies when a notification arrives.

### Step 3 — Wire into hosted service (ASP.NET Core)

```csharp
public class NotificationPublisherHostedService : IHostedService, IDisposable
{
    private readonly NotificationBasedPublisher _publisher;

    public NotificationPublisherHostedService(
        EntityChangeTracker tracker,
        IConfiguration config,
        ILoggerFactory loggerFactory)
    {
        _publisher = new NotificationBasedPublisher(
            tracker,
            new NotificationBasedPublisherOptions
            {
                ConnectionString = config.GetConnectionString("Default")!,
                ChannelName = "products_notify",
                FallbackPollingInterval = TimeSpan.FromSeconds(30)
            },
            loggerFactory);
    }

    public Task StartAsync(CancellationToken ct) => _publisher.StartAsync(ct);
    public Task StopAsync(CancellationToken ct) => _publisher.StopAsync(ct);
    public void Dispose() => _publisher.Dispose();
}
```

```csharp
builder.Services.AddHostedService<NotificationPublisherHostedService>();
```

## NotificationBasedPublisherOptions

| Property | Type | Default | Description |
|---|---|---|---|
| `ConnectionString` | `string` | _(required)_ | Dedicated connection used for `LISTEN` |
| `ChannelName` | `string` | `"entity_changes"` | PostgreSQL notification channel name — must match the outbox trigger |
| `FallbackPollingInterval` | `TimeSpan` | `30s` | How often to scan for unpublished changes when no notification arrives |

## Manual Trigger DDL

If you need to create or drop the trigger outside of auto-initialization (e.g., in a migration script), use the static helpers on `NotificationBasedPublisher`:

```csharp
// Generate the PLPGSQL notify function
string functionDdl = NotificationBasedPublisher.GenerateNotifyTriggerFunction(
    functionName: "notify_products_outbox_change",
    channelName:  "products_notify");

// Generate the trigger that calls that function
string triggerDdl = NotificationBasedPublisher.GenerateNotifyTrigger(
    triggerName:     "products_outbox_notify_trigger",
    outboxTableName: "products_outbox",
    functionName:    "notify_products_outbox_change");

// Drop the trigger (before re-creating it)
string dropDdl = NotificationBasedPublisher.GenerateDropTrigger(
    triggerName:     "products_outbox_notify_trigger",
    outboxTableName: "products_outbox");
```

Resulting SQL (for reference):

```sql
-- Trigger function
CREATE OR REPLACE FUNCTION notify_products_outbox_change()
RETURNS TRIGGER AS $$
BEGIN
    PERFORM pg_notify('products_notify', json_build_object(
        'entity_type', NEW.entity_type,
        'id',          NEW.id,
        'change_type', NEW.change_type
    )::text);
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Trigger
CREATE TRIGGER products_outbox_notify_trigger
    AFTER INSERT ON products_outbox
    FOR EACH ROW EXECUTE FUNCTION notify_products_outbox_change();

-- Drop (use before re-creating)
DROP TRIGGER IF EXISTS products_outbox_notify_trigger ON products_outbox;
```

## Multiple Entity Types

Each entity type can use its own channel, or they can share one:

```csharp
// Separate channels per entity
var productOptions = new PostgreSqlOutboxOptions { ... }
    .UseNotificationChannel("products_notify");

var orderOptions = new PostgreSqlOutboxOptions { ... }
    .UseNotificationChannel("orders_notify");

// A publisher per channel (ILoggerFactory required as third argument)
var productPublisher = new NotificationBasedPublisher(tracker, new() { ChannelName = "products_notify", ... }, loggerFactory);
var orderPublisher   = new NotificationBasedPublisher(tracker, new() { ChannelName = "orders_notify",   ... }, loggerFactory);
```

## Monitoring

### Verify the trigger exists

```sql
SELECT trigger_name, event_object_table, action_statement
FROM information_schema.triggers
WHERE event_object_table = 'products_outbox';
```

### Verify the function exists

```sql
\df notify_products_outbox_change
```

### Test a notification manually

```sql
LISTEN products_notify;
NOTIFY products_notify, '{"test": true}';
-- psql prints: Asynchronous notification "products_notify" received from server process ...
```

## Troubleshooting

**Not receiving notifications**
- Check that `UseNotificationChannel = true` was set before calling `Build()` / `BuildAsync()`
- Verify the channel name matches between the outbox options and `NotificationBasedPublisherOptions.ChannelName`
- Confirm the trigger exists with the query above
- Check PostgreSQL logs for trigger execution errors

**Changes delivered twice**
- The fallback polling loop and the notification handler both call `MarkPublishedAsync` after delivery; a change is skipped if already marked published (`Published = true`), so duplicates should not occur in normal operation
- If you see duplicates, check whether multiple `NotificationBasedPublisher` instances are listening on the same channel for the same entity type

**Notification channel name mismatch**
- The channel name in `PostgreSqlOutboxOptions.NotificationChannel` (used when creating the trigger) must exactly match `NotificationBasedPublisherOptions.ChannelName` (used for `LISTEN`)
