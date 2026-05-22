# RabbitMQ Microservices Example

A two-microservice example showing how to stream entity changes across services using **RayTree** with PostgreSQL as the outbox and entity store, RabbitMQ as the broker, MessagePack as the serializer, and Gzip as the compressor.

```
OrderService                              NotificationService
─────────────                             ─────────────────────
PostgreSqlRepository<Order>   →           queue: notification-service.orders
  + EntityChangeTracker        ┐          bound to: change.Order.*
  + PostgreSqlOutbox<Order>    │
  + RabbitMqPublisher          │
                               ▼
        ┌──── topic exchange: raytree.changes ────┐
        │   keys: change.Order.{insert|update|delete}
        └──────────────────────────────────────────┘
```

`OrderService` continuously inserts/updates/deletes `Order` rows. Each change is written to a PostgreSQL outbox table and picked up by the publisher loop, serialized with MessagePack, compressed with Gzip, and published to RabbitMQ. `NotificationService` subscribes to the topic exchange, decompresses + deserializes, and dispatches to per-`ChangeType` handlers that log to the console via `ILogger`.

> **Note:** this example is intentionally **not** part of `RayTree.slnx`. To open it in an IDE, open `examples/RabbitMQ.Microservices/RabbitMQ.Microservices.slnx` directly.

---

## Prerequisites

| Tool | Version |
|---|---|
| Docker (with Compose v2) | 24+ |
| .NET SDK | 10.0+ (local dev only — not needed for `docker compose up`) |

---

## Running with Docker Compose

From `examples/RabbitMQ.Microservices/`:

```bash
docker compose up --build
```

Both services start after RabbitMQ passes its healthcheck (`rabbitmq-diagnostics ping`). `OrderService` additionally waits for PostgreSQL. `NotificationService` does not depend on `OrderService` — it probes the exchange via `WaitForTopology = true` and begins consuming as soon as `OrderService` declares it.

**Expected console output** (interleaved, simplified — log lines include level and category):

```
order-service-1         | info: Inserted order 5a1c... for Alice totalling $342.18
order-service-1         | info: Inserted order 7b2d... for Bob totalling $87.00
notification-service-1  | info: [NOTIFY] NEW order 5a1c... — customer=Alice total=$342.18 status=Pending
notification-service-1  | info: [NOTIFY] NEW order 7b2d... — customer=Bob total=$87.00 status=Pending
order-service-1         | info: Updated order 5a1c... → status=Shipped total=$348.92
notification-service-1  | info: [NOTIFY] UPDATED order 5a1c... — status=Shipped total=$348.92
order-service-1         | info: Deleted order 7b2d...
notification-service-1  | info: [NOTIFY] DELETED order 7b2d...
```

To stop cleanly:

```bash
docker compose down
```

Both named volumes (`postgres-data`, `rabbitmq-data`) are preserved on restart. To wipe them:

```bash
docker compose down -v
```

---

## Connection Details (for Local Dev)

| Service | Address |
|---|---|
| RabbitMQ broker | `localhost:5672` |
| RabbitMQ management UI | `http://localhost:15672` — login `guest` / `guest` |
| PostgreSQL | `localhost:5432` — database `raytree_example`, user `postgres`, password `postgres` |

Override with environment variables when running outside Compose:

```bash
# OrderService (requires a running PostgreSQL and RabbitMQ)
RABBITMQ_HOST=localhost \
POSTGRES_CONNECTION="Host=localhost;Port=5432;Database=raytree_example;Username=postgres;Password=postgres" \
dotnet run --project OrderService

# NotificationService (requires a running RabbitMQ; no PostgreSQL dependency)
RABBITMQ_HOST=localhost \
dotnet run --project NotificationService
```

---

## What to Look at

In the RabbitMQ management UI (`http://localhost:15672`):
- Inspect the `raytree.changes` topic exchange and the `notification-service.orders` queue.
- Watch live message rates on the queue's **Message rates** chart.
- Browse a single payload under **Get messages** to see the compressed MessagePack bytes (the body is binary, not readable text).

In PostgreSQL (`localhost:5432`, database `raytree_example`):
- `orders` — the entity table; one row per live order.
- `order_outbox` — the staging table; `published = true` rows have been sent to RabbitMQ and will be cleaned up by the outbox rotation loop.

---

## Project Structure

```
examples/RabbitMQ.Microservices/
├── RabbitMQ.Microservices.slnx   # Standalone solution — open this in your IDE
├── Directory.Build.props          # Inherits from repo root; sets IsPackable=false
├── Directory.Packages.props       # Inherits from repo root; adds Microsoft.Extensions.Hosting
├── docker-compose.yml             # Postgres + RabbitMQ + OrderService + NotificationService
│
├── Shared/
│   ├── Shared.csproj
│   └── Order.cs                   # Shared entity — [Table("orders")], [Key] Guid Id, ...
│
├── OrderService/
│   ├── OrderService.csproj
│   ├── Program.cs                 # AddChangeTracking: outbox → RabbitMqPublisher
│   ├── OrderSimulator.cs          # BackgroundService: inserts/updates/deletes in a loop
│   └── Dockerfile                 # Multi-stage; build context = repo root
│
└── NotificationService/
    ├── NotificationService.csproj
    ├── Program.cs                 # AddChangeTracking: RabbitMqConsumer → OnInsert/Update/Delete
    └── Dockerfile                 # Multi-stage; build context = repo root
```

---

## How It Works

```
OrderSimulator
  → IRepository<Order>.InsertAsync / UpdateAsync / DeleteAsync   (PostgreSQL: orders table)
  → EntityChangeTracker.TrackInsertAsync / TrackUpdateAsync / TrackDeleteAsync
      → PostgreSqlOutbox<Order>                                   (PostgreSQL: order_outbox table)
          ↑ polled every 500 ms by OutboxPublisherService
              → MessagePack serialize → Gzip compress → MessageEnvelope
                  → RabbitMqPublisher  →  exchange: raytree.changes  (routing key: change.Order.<type>)
                      → RabbitMqConsumer (NotificationService)
                          → Gzip decompress → MessagePack deserialize
                              → OnInsert / OnUpdate / OnDelete handlers → ILogger
```

### Startup sequencing — `WaitForTopology`

`NotificationService` does **not** have a `depends_on: order-service` in `docker-compose.yml`. Instead, `RabbitMqConsumerOptions.WaitForTopology = true` makes the consumer probe the exchange (`raytree.changes`) via AMQP passive declares and retry every 5 seconds until `OrderService` declares it. This correctly decouples startup order at the application level rather than relying on Compose container ordering, and it mirrors how the services would behave in a Kubernetes or ECS deployment where container scheduling is non-deterministic.

---

## Known Limitations

**The example is not transactionally safe between the entity table and the outbox.**

`OrderSimulator` calls `repository.InsertAsync(order)` and then `tracker.TrackInsertAsync(order)` — two separate database round-trips. A crash between them can leave `orders` and `order_outbox` inconsistent.

**Production path:** use `RayTree.EntityFrameworkCore` and `EntityChangeInterceptor`, which hooks into `SaveChangesAsync` and calls `TrackXxxAsync` inside the same EF Core transaction, making both writes atomic. See `src/RayTree.EntityFrameworkCore` for the implementation.

---

## Going Further

### NOTIFY/LISTEN fast-path

The default 500 ms poll interval adds latency. Enable the PostgreSQL `NOTIFY`/`LISTEN` fast path to publish within milliseconds of the outbox write:

```csharp
.UseOutbox(new PostgreSqlOutbox<Order>(new PostgreSqlOutboxOptions
{
    ConnectionString = pgConnection,
    OutboxTableName = "order_outbox",
    UseNotificationChannel = true,
    NotificationChannel = "order_outbox_notify",
}, pluginLoggerFactory))
```

The poll loop becomes a safety net; the notification channel drives normal delivery.

### At-least-once delivery

The example uses at-most-once delivery (the default): the broker ACK is sent immediately when the message is received, before any handler runs. To switch to at-least-once:

```csharp
.UseConsumer(new RabbitMqConsumer(new RabbitMqConsumerOptions
{
    ...
    AckAfterHandler = true,   // ACK deferred until all handlers succeed
}))
```

On handler retry-exhaustion the consumer issues `BasicNack(requeue: true)`, returning the message to the queue for redelivery.

### Topology wait (topology owned by another service)

In the example, `OrderService` declares the exchange (`DeclareExchange = true`) and `NotificationService` waits for it (`WaitForTopology = true`). If your topology is owned by a third service (e.g. a dedicated infrastructure bootstrap), apply `WaitForTopology` on the publisher side too:

```csharp
.UsePublisher(new RabbitMqPublisher(new RabbitMqPublisherOptions
{
    ExchangeName     = "raytree.changes",
    DeclareExchange  = false,          // owned externally
    WaitForTopology  = true,
    TopologyWaitTimeout = TimeSpan.FromMinutes(5),
}))
```

### Isolated-handler dispatch (one subscription per handler)

The example uses **shared-handler** mode: a single `RabbitMqConsumer` delivers each message to all handlers in sequence. For independent downstream systems each needing their own queue, use `UseConsumerFactory`:

```csharp
.UseConsumerFactory(handlerName => new RabbitMqConsumer(new RabbitMqConsumerOptions
{
    HostName = rmqHost,
    QueueName = $"notification-service.orders.{handlerName}",
    ExchangeName = "raytree.changes",
    BindingKey = "change.Order.*",
    DeclareQueue = true,
    Durable = true,
}))
.OnInsert("email-handler", SendEmailAsync)
.OnUpdate("audit-handler", WriteAuditLogAsync)
```

Each named handler gets its own queue and consumer — offsets and retry budgets are isolated.

### Distributed deduplication

The default in-memory deduplication store does not survive restarts. For durable, cross-replica dedup swap in Redis:

```csharp
builder.Services.AddChangeTracking(builder.Configuration, cfg =>
    cfg.UseDeduplicationStore(new RedisDeduplicationStore("localhost:6379")));
```

### OpenTelemetry

Add `RayTree.OpenTelemetry` and call `meterProvider.AddRayTreeMetrics()` to export the 18 RayTree instruments (outbox lag, publish duration, handler attempts, payload size, queue depth) to your OTel collector.
