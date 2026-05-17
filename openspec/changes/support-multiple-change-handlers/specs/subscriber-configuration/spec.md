## MODIFIED Requirements

### Requirement: Subscriber queue configuration example
The fluent configuration for subscribers SHALL select a handler-dispatch mode by which consumer-binding method is called on `IEntityBuilder<TEntity>`. `UseConsumer(IQueueConsumer)` selects `Shared` mode and forks the chain into `ISharedHandlerBuilder<TEntity>`; `UseConsumerFactory(Func<string, IQueueConsumer>)` selects `Isolated` mode and forks the chain into `IIsolatedHandlerBuilder<TEntity>`. Handler-registration methods (`OnInsert`, `OnUpdate`, `OnDelete`, `OnChange`) are only available on the post-fork builders.

The following example illustrates both modes side-by-side in an ASP.NET Core application:

```csharp
// Program.cs
var auditConsumer = new InMemoryQueue();  // single consumer for Shared-mode entity

builder.Services
    .AddChangeTracking(builder.Configuration, ct => ct
        .UseOutbox<PostgreSqlOutbox<Order>>(t =>
            new PostgreSqlOutbox<Order>(pgOptions, loggerFactory))
        .UsePublisher<KafkaPublisher>(t => new KafkaPublisher(kafkaPubOptions))
        .UseSerializer<JsonSerializerPlugin>(t => new JsonSerializerPlugin())

        // Shared mode — one delivery shared by all handlers, in-process sequential dispatch.
        .ForEntity<AuditLog>(e => e
            .UseConsumer(auditConsumer)                       // returns ISharedHandlerBuilder<AuditLog>
                .OnInsert(async (change, ct) =>               // anonymous — no name required
                {
                    await sink.AppendAsync(change, ct);
                })
                .OnInsert(async (change, ct) =>               // second handler — accumulates, does not replace
                {
                    await metrics.RecordAsync(change, ct);
                }))

        // Isolated mode — each named handler has its own broker subscription, retry,
        // and dedup namespace. The factory is invoked once per unique handler name.
        .ForEntity<Order>(e => e
            .UseConsumerFactory(handlerName => new KafkaConsumer(
                kafkaConsumerOptions with { GroupId = $"orders-{handlerName}" },
                loggerFactory))                               // returns IIsolatedHandlerBuilder<Order>
                .OnInsert("read-model", async (change, ct) =>
                {
                    await readModel.UpsertAsync(change.State!, ct);
                })
                .OnInsert("notifier", async (change, ct) =>
                {
                    await notifier.PublishCreatedAsync(change.EntityId, ct);
                })
                .OnUpdate("read-model", async (change, ct) =>
                {
                    await readModel.UpsertAsync(change.State!, ct);
                }))

        .UseRedisDeduplication("localhost:6379"));
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

`ChangeTrackingHostedService` SHALL start one consume loop per entity in `Shared` mode and one consume loop per `(entity, handlerName)` pair in `Isolated` mode, with no additional wiring required from the caller.

The compiler SHALL prevent the following misconfigurations:
- Calling `OnInsert` (or any handler method) on `IEntityBuilder<TEntity>` before binding a consumer — the methods do not exist on that interface.
- Calling the anonymous `OnInsert(handler)` overload after `UseConsumerFactory` — only named overloads exist on `IIsolatedHandlerBuilder<TEntity>`.
- Calling the named `OnInsert(handlerName, handler)` overload after `UseConsumer` — only anonymous overloads exist on `ISharedHandlerBuilder<TEntity>`.
- Calling both `UseConsumer` and `UseConsumerFactory` in the same chain — the post-fork interface does not expose the other binding method.

Handler-name uniqueness within an `Isolated` entity SHALL be validated at `Build()` time and throw `InvalidOperationException` on duplicate `(action, handlerName)` pairs.
