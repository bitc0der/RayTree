## ADDED Requirements

### Requirement: Queue source registered per entity in fluent configuration
Each entity SHALL have its queue source registered through the fluent configuration builder so that the subscriber knows where to consume messages from.

#### Scenario: Register InMemoryQueue as consume source
- **WHEN** `.UseQueue<Order>(queue)` is called on the subscriber configuration builder
- **THEN** the subscriber SHALL consume `Order` messages from that `InMemoryQueue` instance

#### Scenario: Register queue source with entity in DI setup
- **WHEN** `AddChangeSubscriber()` is used and `.UseQueue<Order>(queue)` is called on the returned builder
- **THEN** the DI-registered `ChangeSubscriber` SHALL be bound to that queue for `Order` messages

#### Scenario: Multiple entities with independent queue sources
- **WHEN** different entities are each configured with `.UseQueue<T>(queue)` using separate queue instances
- **THEN** each entity SHALL consume independently from its own queue source

### Requirement: Hosted service SHALL auto-start consumption for all registered queues
When `ChangeSubscriberHostedService` starts, it SHALL begin consuming from every queue registered via the fluent configuration.

#### Scenario: StartAsync launches consume loops
- **WHEN** the ASP.NET Core host starts and `ChangeSubscriberHostedService.StartAsync` is called
- **THEN** a consume loop SHALL begin for each registered entity queue, running as background tasks

#### Scenario: StopAsync cancels all consume loops
- **WHEN** `StopAsync` is called on the hosted service
- **THEN** all running consume loops SHALL be cancelled and drained gracefully before returning

### Requirement: DI-registered options and deduplication store applied to subscriber
The `ChangeSubscriber` instance built by `AddChangeSubscriber` SHALL use the `SubscriberOptions` and `IDeduplicationStore` registered in the DI container.

#### Scenario: Options from configuration applied
- **WHEN** `SubscriberOptions` is bound from `IConfiguration` (section `ChangeTracking:Subscriber`)
- **THEN** the `ChangeSubscriber` instance SHALL use those options (MaxRetries, RetryDelay, SkipOnFailure, etc.)

#### Scenario: Deduplication store from DI applied
- **WHEN** an `IDeduplicationStore` is registered in DI (e.g. via `.UseRedisDeduplication()`)
- **THEN** the `ChangeSubscriber` instance SHALL use that store for deduplication, not the default in-memory store

### Requirement: Subscriber queue configuration example
The following example illustrates the complete queue configuration for a subscriber in an ASP.NET Core application:

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
        // change.State is the fully-typed Order after insertion
        Console.WriteLine($"New order: {change.EntityId}, total: {change.State?.Total}");
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

The `ChangeSubscriberHostedService` registered by `AddChangeSubscriber` SHALL automatically start consuming from `orderQueue` when the host starts, with no additional wiring required from the caller.
