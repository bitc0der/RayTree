# Kafka Microservices Example

This example shows **RayTree's outbox-to-Kafka pipeline** using two cooperating microservices:

- **OrderService** — inserts, updates, and deletes `Order` rows in PostgreSQL; writes every change to a PostgreSQL outbox; and publishes each outbox record to the Kafka topic `raytree.order_changes`.
- **NotificationService** — consumes from that topic and logs each change to the console.

The example mirrors `examples/RabbitMQ.Microservices` but uses Kafka (KRaft, no Zookeeper) and PostgreSQL as the backing store.

> **Note:** this example is intentionally **not** part of `RayTree.slnx`. To open it in an IDE, open `examples/Kafka.Microservices/Kafka.Microservices.slnx` directly.

---

## Prerequisites

| Tool | Version |
|---|---|
| Docker (with Compose v2) | 24+ |
| .NET SDK | 10.0+ (local dev only — not needed for `docker compose up`) |

---

## Running with Docker Compose

```bash
# from the repository root
cd examples/Kafka.Microservices
docker compose up --build
```

Both services start after Kafka passes its healthcheck (TCP probe on port 9092 with a 60-second start window that accommodates KRaft controller-election latency). `OrderService` additionally waits for PostgreSQL to be ready.

**Expected console output** (interleaved from both services):

```
order-service          | Inserted order a1b2… for Alice totalling $213.45
order-service          | Inserted order c3d4… for Bob totalling $87.00
notification-service   | [NOTIFY] NEW order a1b2… — customer=Alice total=$213.45 status=Pending
notification-service   | [NOTIFY] NEW order c3d4… — customer=Bob total=$87.00 status=Pending
order-service          | Updated order a1b2… → status=Confirmed total=$231.00
notification-service   | [NOTIFY] UPDATED order a1b2… — status=Confirmed total=$231.00
order-service          | Deleted order c3d4…
notification-service   | [NOTIFY] DELETED order c3d4…
```

To stop cleanly:

```bash
docker compose down
```

PostgreSQL data is preserved in the named volume `postgres-data`. To wipe it:

```bash
docker compose down -v
```

---

## Connection Details (for Local Dev)

| Service | Address |
|---|---|
| Kafka broker | `localhost:9092` |
| PostgreSQL | `localhost:5432` — database `raytree_example`, user `postgres`, password `postgres` |

Override with environment variables when running outside Compose:

```bash
# OrderService (requires a running PostgreSQL and Kafka)
KAFKA_BOOTSTRAP_SERVERS=localhost:9092 \
POSTGRES_CONNECTION="Host=localhost;Port=5432;Database=raytree_example;Username=postgres;Password=postgres" \
dotnet run --project OrderService

# NotificationService (requires a running Kafka; no PostgreSQL dependency)
KAFKA_BOOTSTRAP_SERVERS=localhost:9092 \
dotnet run --project NotificationService
```

---

## Project Structure

```
examples/Kafka.Microservices/
├── Kafka.Microservices.slnx          # Standalone solution — open this in your IDE
├── Directory.Build.props             # Inherits from repo root; sets IsPackable=false
├── Directory.Packages.props          # Inherits from repo root; adds Microsoft.Extensions.Hosting
├── docker-compose.yml                # Postgres + Kafka + OrderService + NotificationService
│
├── Shared/
│   ├── Shared.csproj
│   └── Order.cs                      # Shared entity — [Table("orders")], [Key] Guid Id, ...
│
├── OrderService/
│   ├── OrderService.csproj
│   ├── Program.cs                    # AddChangeTracking: outbox → KafkaPublisher
│   ├── OrderSimulator.cs             # BackgroundService: inserts/updates/deletes in a loop
│   └── Dockerfile                    # Multi-stage; build context = repo root
│
└── NotificationService/
    ├── NotificationService.csproj
    ├── Program.cs                    # AddChangeTracking: KafkaConsumer → OnInsert/Update/Delete
    └── Dockerfile                    # Multi-stage; build context = repo root
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
                  → KafkaPublisher  →  topic: raytree.order_changes  (partition key: Order:<id>)
                      → KafkaConsumer (NotificationService)
                          → Gzip decompress → MessagePack deserialize
                              → OnInsert / OnUpdate / OnDelete handlers → Console.WriteLine
```

### Why an outbox?

Publishing directly to Kafka inside the same database transaction is not possible — Kafka is not a transactional resource that participates in two-phase commit. The outbox table acts as a durable staging area: `TrackXxxAsync` writes to it atomically with the entity change (or in a logically equivalent step), then a background polling loop reads the outbox and publishes to Kafka, marking records published once the broker confirms receipt. This guarantees that no change is silently lost even if the process crashes between the DB write and the Kafka send.

### Partition key

The default `KeySelector` produces `"{EntityType}:{EntityId}"` — all events for the same `Order` ID land on the same Kafka partition, preserving per-entity ordering. With `KAFKA_NUM_PARTITIONS=3` in this example, different order IDs will spread across partitions, so you can observe the routing in action.

---

## Known Limitations

The `OrderSimulator` calls the repository and the outbox as two **separate, non-atomic** operations:

```csharp
await _repository.InsertAsync(order, ct);   // 1. Write to PostgreSQL orders table
await _tracker.TrackInsertAsync(order, ct); // 2. Write to PostgreSQL order_outbox table
```

A process crash between steps 1 and 2 leaves the entity in the `orders` table without a corresponding outbox record — the change is never published to Kafka.

**Production path:** use `RayTree.EntityFrameworkCore` and `EntityChangeInterceptor`, which hooks into `SaveChangesAsync` and calls `TrackXxxAsync` inside the same EF Core transaction, making both writes atomic. See the `EntityChangeInterceptor` class in `src/RayTree.EntityFrameworkCore` for the implementation.

---

## Going Further

### NOTIFY/LISTEN fast-path

The default 500 ms poll interval is readable but adds latency. Enable the PostgreSQL `NOTIFY`/`LISTEN` fast path to publish within milliseconds of the outbox write:

```csharp
.UseOutbox(new PostgreSqlOutbox<Order>(new PostgreSqlOutboxOptions
{
    ConnectionString = pgConnection,
    OutboxTableName = "order_outbox",
    UseNotificationChannel = true,   // trigger fires pg_notify on INSERT into outbox
    NotificationChannel = "order_outbox_notify",
}, pluginLoggerFactory))
```

The poll loop becomes a safety net (catches anything the NOTIFY path misses); the notification channel drives normal delivery.

### At-least-once delivery

The example uses at-most-once delivery (the default): Kafka offsets are committed immediately after the message is parsed, before the handler runs. If the process crashes mid-handler the message is not redelivered.

To switch to at-least-once:

```csharp
.UseConsumer(new KafkaConsumer(new KafkaConsumerOptions
{
    BootstrapServers = kafkaBootstrap,
    Topic = "raytree.order_changes",
    GroupId = "notification-service",
    AckAfterHandler = true,   // commit after handler confirms success
}, pluginLoggerFactory))
```

> **Important:** also set `SubscriberOptions.MaxDegreeOfParallelism = 1` (the default). Kafka offset commits are monotonic — committing a higher offset first would advance past any in-flight lower-offset messages, silently skipping them on restart and defeating the guarantee. Sequential processing avoids this entirely.

### Custom partition key (sharding by tenant / aggregate root)

```csharp
.UsePublisher(new KafkaPublisher(new KafkaPublisherOptions
{
    BootstrapServers = kafkaBootstrap,
    Topic = "raytree.order_changes",
    KeySelector = envelope => envelope.Metadata.TryGetValue("TenantId", out var tid)
        ? tid
        : $"{envelope.EntityType}:{envelope.EntityId}",
}))
```

### Isolated-handler dispatch (one consumer group per handler)

The example uses **shared-handler** mode: a single `KafkaConsumer` delivers each message to all handlers in sequence. For independent downstream systems each requiring their own offset tracking, use **isolated-handler** mode with `UseConsumerFactory`:

```csharp
.UseConsumerFactory(handlerName => new KafkaConsumer(new KafkaConsumerOptions
{
    BootstrapServers = kafkaBootstrap,
    Topic = "raytree.order_changes",
    GroupId = $"notification-service-{handlerName}",
}, pluginLoggerFactory))
.OnInsert("email-handler", SendEmailAsync)
.OnUpdate("audit-handler", WriteAuditLogAsync)
```

Each named handler gets its own subscription and Kafka consumer group — offsets advance independently.

---

## Consumer-Group Scaling

Because all `NotificationService` replicas share the same `GroupId = "notification-service"`, Kafka automatically rebalances the three partitions across them.

Start two replicas:

```bash
docker compose up --scale notification-service=2
```

Watch the logs: each replica logs which partitions it was assigned after the rebalance. With 3 partitions and 2 replicas, one replica handles 2 partitions and the other handles 1. Adding a third replica gives each replica exactly one partition.

A fourth replica receives no partitions (Kafka never assigns more partitions than it has to a single consumer group). Scale beyond the partition count only when you need hot standbys for failover.

---

## Partition-Key Behaviour

Each `Order` is assigned a Kafka partition based on its ID via the default key `"Order:<id>"`. To inspect which messages landed on which partition, use the Kafka console consumer directly against the running container:

```bash
# List messages on partition 0
docker exec kafka /opt/kafka/bin/kafka-console-consumer.sh \
  --topic raytree.order_changes \
  --partition 0 \
  --bootstrap-server localhost:9092 \
  --from-beginning \
  --max-messages 20
```

Repeat with `--partition 1` and `--partition 2`. You should observe:
- All events for a given `Order.Id` appear on exactly one partition.
- Different `Order.Id`s spread across the three partitions according to the Murmur2 hash of the key.

To remove the partition-key guarantee (round-robin distribution), set `KeySelector = _ => string.Empty` in `KafkaPublisherOptions`. Per-entity ordering is then no longer preserved.

---

## `FromEarliest = true` — How It Works

`KafkaConsumerOptions.FromEarliest = true` (the default) means that when a **new** consumer group first connects to a topic, it begins reading from offset 0 — the very beginning of the partition log. Once it commits an offset, subsequent restarts of the same group resume from the last committed offset, not from the beginning.

This is why `notification-service` has no `depends_on: order-service` in `docker-compose.yml`. The sequence is safe:

1. `notification-service` starts, subscribes to `raytree.order_changes`, finds no messages yet, waits.
2. `order-service` starts, publishes messages starting from offset 0.
3. `notification-service` reads from offset 0 and processes every message — nothing is missed.

If you restart `notification-service` after it has processed some messages, it resumes from its last committed offset rather than replaying from the beginning.
