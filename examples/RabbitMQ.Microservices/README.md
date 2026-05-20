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

`OrderService` continuously inserts/updates/deletes `Order` rows. Each change is written to a PostgreSQL outbox table and picked up by the publisher loop, serialized with MessagePack, compressed with Gzip, and published to RabbitMQ. `NotificationService` subscribes to the topic exchange, decompresses + deserializes, and dispatches to per-`ChangeType` handlers that simply log to stdout.

This example is intentionally **not** part of the main `RayTree.slnx` solution. Open `examples/RabbitMQ.Microservices/RabbitMQ.Microservices.slnx` directly.

## Prerequisites

- Docker / Docker Compose
- .NET 10 SDK (only required if you want to build / debug outside Docker)

## Run

From `examples/RabbitMQ.Microservices/`:

```bash
docker compose up --build
```

Expected console output (interleaved between the two services):

```
order-service-1         | Inserted order 5a1c... for Alice totalling $342.18
notification-service-1  | [NOTIFY] NEW order 5a1c... — customer=Alice total=$342.18 status=Pending
order-service-1         | Updated order 5a1c... → status=Shipped total=$348.92
notification-service-1  | [NOTIFY] UPDATED order 5a1c... — status=Shipped total=$348.92
order-service-1         | Deleted order 5a1c...
notification-service-1  | [NOTIFY] DELETED order 5a1c...
```

Stop with `Ctrl+C` then `docker compose down` to remove containers.

## What to look at

| Surface | URL / location |
|---|---|
| RabbitMQ management UI | `http://localhost:15672` (login `guest` / `guest`) |
| PostgreSQL | `localhost:5432`, db `raytree_example`, user/pass `postgres`/`postgres` |
| Outbox table | `order_outbox` — see published vs unpublished rows |
| Entity table | `orders` — keys-only journal (the outbox payload carries the full state) |

In RabbitMQ's management UI you can:
- Inspect the `raytree.changes` topic exchange and the `notification-service.orders` queue.
- Watch live message rates.
- Browse a single payload to see the compressed MessagePack bytes.

## Project structure

```
examples/RabbitMQ.Microservices/
├── Directory.Build.props        # Imports root props, disables packaging for console apps
├── Directory.Packages.props     # Imports root props, adds Microsoft.Extensions.Hosting
├── RabbitMQ.Microservices.slnx  # Standalone solution — NOT in RayTree.slnx
├── docker-compose.yml
├── README.md
├── Shared/
│   ├── Shared.csproj
│   └── Order.cs                 # POCO with [Table("orders")] + [Key] Id (no MessagePack attrs)
├── OrderService/
│   ├── OrderService.csproj
│   ├── Dockerfile
│   ├── Program.cs               # Host.CreateApplicationBuilder + AddChangeTracking
│   └── OrderSimulator.cs        # BackgroundService that drives the demo
└── NotificationService/
    ├── NotificationService.csproj
    ├── Dockerfile
    └── Program.cs               # Shared-handler consumer with OnInsert/OnUpdate/OnDelete
```

## Configuration

Both services read connection info from environment variables:

| Variable | Default (localhost) | Compose value |
|---|---|---|
| `POSTGRES_CONNECTION` | `Host=localhost;Port=5432;Database=raytree_example;Username=postgres;Password=postgres` | `Host=postgres;…` |
| `RABBITMQ_HOST` | `localhost` | `rabbitmq` |

## Known limitations

**The example is not transactionally safe between the entity table and the outbox.**

`OrderSimulator` calls `repository.InsertAsync(order)` and then `tracker.TrackInsertAsync(order)` — two separate database round-trips. A crash between them can leave `orders` and `order_outbox` inconsistent. The outbox pattern usually relies on a single transaction wrapping both writes; that's exactly what `RayTree.EntityFrameworkCore` (specifically the `EntityChangeInterceptor`) does inside `SaveChangesAsync`. Production code targeting transactional safety should use that integration, **not** this example's pattern.

This example deliberately omits EF Core to keep the focus on RabbitMQ wiring, but the limitation is real — please don't copy `OrderSimulator` into a service that needs durable consistency.

## Going further

- **NOTIFY/LISTEN fast-path** — set `PostgreSqlOutboxOptions.UseNotificationChannel = true` to get sub-100 ms publish latency instead of polling. A DB trigger fires `pg_notify` on every outbox INSERT; the publisher subscribes and claims rows atomically.
- **At-least-once delivery** — set `RabbitMqConsumerOptions.AckAfterHandler = true` so the broker ACK is deferred until all handlers complete. Combined with the deduplication store, this yields effectively-once semantics.
- **Isolated-handler dispatch** — replace `e.UseConsumer(...)` with `e.UseConsumerFactory(name => new RabbitMqConsumer(...))` to give each named handler its own broker subscription, retry budget, and dedup namespace.
- **Distributed deduplication** — swap the default in-memory dedup store for `RedisDeduplicationStore` so processed `correlationId`s survive restarts and are visible across replicas.
- **OpenTelemetry** — add `RayTree.OpenTelemetry` and call `meterProvider.AddRayTreeMetrics()` to export the 18 RayTree instruments (outbox lag, publish duration, handler attempts, etc.) to your OTel collector.
