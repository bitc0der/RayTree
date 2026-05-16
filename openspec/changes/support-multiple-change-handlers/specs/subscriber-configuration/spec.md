## MODIFIED Requirements

### Requirement: Subscriber queue configuration example
The following example illustrates the complete queue configuration for a subscriber in an ASP.NET Core application, including multiple handlers registered for the same action type:

```csharp
// Program.cs
var orderQueue = new InMemoryQueue(); // or a RabbitMQ/Kafka queue instance

builder.Services
    .AddChangeSubscriber(builder.Configuration)
    .ConsumeEntity<Order>()
    .UseQueue<Order>(orderQueue)
    .UseSerializer<Order>(new JsonSerializerPlugin())
    .UseCompressor<Order>(new GzipCompressorPlugin())
    .OnInsert<Order>(async (change, ct) =>
    {
        // First handler: update the read model
        Console.WriteLine($"New order: {change.EntityId}, total: {change.State?.Total}");
    })
    .OnInsert<Order>(async (change, ct) =>
    {
        // Second handler: send a notification — registered independently, invoked after the first
        Console.WriteLine($"Notify: order {change.EntityId} created");
    })
    .OnUpdate<Order>(async (change, ct) =>
    {
        Console.WriteLine($"Order updated: {change.EntityId}");
    })
    .OnDelete<Order>(async (change, ct) =>
    {
        // change.State holds the Order state before deletion
        Console.WriteLine($"Order deleted: {change.EntityId}");
    })
    .UseRedisDeduplication("localhost:6379");
```

`appsettings.json`:
```json
{
  "ChangeTracking": {
    "Subscriber": {
      "MaxRetries": 3,
      "RetryDelay": "00:00:01",
      "SkipOnFailure": false,
      "DeduplicationRetention": "24:00:00"
    }
  }
}
```

The `ChangeSubscriberHostedService` registered by `AddChangeSubscriber` SHALL automatically start consuming from `orderQueue` when the host starts, with no additional wiring required from the caller. Each call to `OnInsert`, `OnUpdate`, `OnDelete`, or `OnChange` adds a handler to the entity's handler list — a second call for the same action type does not replace the first.
