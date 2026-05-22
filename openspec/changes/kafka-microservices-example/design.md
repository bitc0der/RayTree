## Context

RayTree has full Kafka support (`KafkaPublisher`, `KafkaConsumer`, configurable partition-key selector, `AckAfterHandler`) but no runnable multi-service example. All existing Kafka coverage lives in integration tests (`RayTree.Plugins.Kafka.Tests`) that use Testcontainers and focus on correctness, not on illustrating real-world wiring. The new RabbitMQ example establishes the pattern; this change mirrors it for Kafka, adapting where the two brokers genuinely differ (topic-based routing vs. exchange/queue, partition keys, consumer groups, KRaft Docker image).

The example must be completely self-contained: it cannot modify library source, must not be compiled as part of the main solution, and must run with a single `docker compose up`.

## Goals / Non-Goals

**Goals:**
- Show a realistic producer microservice (`OrderService`) that tracks `Order` entity changes into a PostgreSQL outbox and publishes to a Kafka topic.
- Show a realistic consumer microservice (`NotificationService`) that consumes order change messages from Kafka and dispatches to typed handlers.
- Demonstrate shared-consumer handler dispatch mode (all handlers on one consumer).
- Include `docker-compose.yml` that starts PostgreSQL, Kafka (KRaft — no Zookeeper), and both services.
- Use `PostgreSqlOutbox<Order>` and `PostgreSqlRepository<Order>` in `OrderService` to demonstrate the durable outbox path.
- Produce `README.md` explaining how to run and what to observe.

**Non-Goals:**
- Isolated-consumer (per-handler subscription) mode — shared mode is sufficient.
- EF Core integration — that belongs in a separate dedicated example.
- OpenTelemetry wiring — out of scope for this example.
- Kafka Streams, KSQL, or Schema Registry — this example stays at the raw producer/consumer level.
- Production-hardened configuration (SSL/SASL, retry policies) — kept simple for readability.
- Changing any library source file or existing test.

## Decisions

### D1: Separate .NET solution file for the example

The example uses its own `Kafka.Microservices.slnx` rather than being added to `RayTree.slnx`. This keeps CI unaffected and makes the example clearly standalone. Projects reference RayTree via `<ProjectReference>` so developers can hack on both simultaneously.

**Alternative considered**: add to main solution. Rejected — same reason as the RMQ example: it couples the example's compile state to every library PR.

### D2: Two console apps in one solution, not two separate repos

`OrderService` and `NotificationService` are both console apps inside `examples/Kafka.Microservices/`, each a `Program.cs` using top-level statements. Keeping them together simplifies the `docker-compose.yml` and README.

### D3: PostgreSQL outbox and repository

`PostgreSqlOutbox<Order>` and `PostgreSqlRepository<Order>` are used for `OrderService`, identical to the RMQ example. The outbox survives restarts, schema migration runs automatically on `InitializeAsync`, and the repository provides typed INSERT/UPDATE/DELETE/SELECT. Both share the same `POSTGRES_CONNECTION` environment variable.

`NotificationService` is subscriber-only — no outbox or repository.

**Alternative considered**: in-memory outbox. Rejected — the user explicitly asked for PostgreSQL storage and outbox.

### D4: Single Kafka topic, default partition key, multi-partition

A single topic `raytree.order_changes` carries all change types (Insert, Update, Delete). The default `KafkaPublisherOptions.KeySelector` (`envelope => $"{envelope.EntityType}:{envelope.EntityId}"`) is used — no custom selector. This guarantees all changes for the same `Order` entity land on the same partition, preserving per-entity ordering.

The broker is configured with `KAFKA_NUM_PARTITIONS=3` so auto-created topics get 3 partitions by default. Without this, `KAFKA_AUTO_CREATE_TOPICS_ENABLE=true` creates topics with a single partition — every message would land on partition 0 regardless of key, and the partition-key story would be vacuously true. Three is the minimum that lets a reader open the Kafka console and visually confirm different entities are spread across partitions.

The topic name uses underscores (`order_changes`) instead of mixing `.` and `-` because Kafka's JMX metric subsystem treats `.` and `_` as interchangeable for metric naming, so mixed separators can produce ambiguous metric names in monitoring tools.

`NotificationService` uses a single `KafkaConsumer` subscribed to `raytree.order_changes` with `GroupId = "notification-service"`. The `ChangeSubscriber` dispatches to Insert / Update / Delete handlers based on `envelope.ChangeType`.

**Alternative considered**: separate topics per change type (`orders.insert`, `orders.update`, `orders.delete`). Rejected — it multiplies topic configuration for no benefit at demo scale; it also prevents per-entity ordering across change types.

**Alternative considered**: custom partition key by `EntityId` only (strip type prefix). Rejected — the default already produces per-entity ordering without extra configuration. Showing the override belongs in a more advanced example.

### D5: Shared-handler dispatch mode

`NotificationService` uses `UseConsumer(consumer)` (shared mode) with `OnInsert`, `OnUpdate`, and `OnDelete` handlers chained on the returned `ISharedHandlerBuilder<Order>`. This mirrors the RMQ example and covers the common case without the boilerplate of `UseConsumerFactory`.

### D6: KRaft mode Kafka image (no Zookeeper)

The `docker-compose.yml` uses `apache/kafka:3.9.0` in KRaft mode — Kafka 3.x+, no separate Zookeeper container. The image tag is pinned (not `:latest`) so the example stays reproducible as upstream releases land. This simplifies the compose file (two backing services instead of three: PostgreSQL + Kafka) and reflects current Kafka best practice.

Topic auto-creation is enabled (`KAFKA_AUTO_CREATE_TOPICS_ENABLE=true`) so neither service needs to pre-create the topic; Kafka creates `raytree.order_changes` on the first produce or subscribe call. `KAFKA_NUM_PARTITIONS=3` ensures the auto-created topic has enough partitions to demonstrate the partition-key behaviour (see D4).

**Healthcheck**: a TCP probe (`nc -z localhost 9092`) with `start_period: 60s`. KRaft brokers can take 10–20 s to complete controller election on cold start, and `kafka-topics.sh` (the obvious alternative) waits for cluster metadata to converge which can add another 10 s. A TCP probe is the lightest reliable signal that the broker is accepting client connections.

**Alternative considered**: Bitnami Kafka image with Zookeeper. Rejected — adds a third container and is the legacy path.

**Alternative considered**: `confluentinc/confluent-local`. Rejected — it bundles Schema Registry and ksqlDB that this example doesn't use, and `apache/kafka` is the official upstream image with smaller surface.

**Alternative considered**: require manual topic creation before running. Rejected — introduces friction that makes the example harder to follow.

**Alternative considered**: `kafka-topics.sh` as the healthcheck. Rejected — slow first-pass convergence and brittle on cold KRaft start.

### D7: `RayTree.Hosting` over raw builder

Both services use `Host.CreateApplicationBuilder(args)` + `services.AddChangeTracking(configuration, configure)` from `RayTree.Hosting`. Same rationale as the RMQ example: graceful shutdown, structured logging, and `IConfiguration` binding without bespoke env-var parsing.

### D8: Repository / outbox atomicity is a known simplification

Identical caveat to the RMQ example. The example is not transactionally safe between the entity table and the outbox. Documented prominently in the README with a pointer to `RayTree.EntityFrameworkCore`.

### D9: Shared class library for the `Order` entity

A `Shared/Shared.csproj` class library holds `Order.cs` with `[Key]`, `[Table("orders")]`, and the four standard properties (`Id`, `CustomerName`, `TotalAmount`, `Status`). Identical structure to the RMQ example so readers can compare the two examples side-by-side without re-learning the entity definition.

### D10: Local Directory.Build.props and Directory.Packages.props that inherit the root

Same solution as the RMQ example. The example needs `Microsoft.Extensions.Hosting` (full, not abstractions-only) and `Confluent.Kafka` (via `RayTree.Plugins.Kafka` transitive reference). The root `Directory.Packages.props` must not be modified. Local files in `examples/Kafka.Microservices/` import the parent via `$([MSBuild]::GetPathOfFileAbove(...))` and add example-only entries.

### D11: MessagePack serializer + Gzip compressor

Same payload pipeline as the RMQ example: `RayTree.Plugins.Serializers.MessagePack` + `RayTree.Plugins.Compressors.Gzip`. Both services register the same pair; the `Order` POCO needs no MessagePack-specific attributes. `MessageEnvelope.Payload` on the broker is `gzip(messagepack(EntityChange<Order>))`.

### D12: At-most-once delivery (default `AckAfterHandler = false`)

The example uses the default offset-commit behaviour: `KafkaConsumer` commits the offset on the poll thread immediately after parsing the message, before dispatch to `ChangeSubscriber` — at-most-once semantics. (This is RayTree's behaviour, not Confluent.Kafka's time-based auto-commit, which is disabled internally by RayTree.) A process crash between commit and handler completion loses the message because the committed offset has already advanced.

This keeps the example simple and avoids the `MaxDegreeOfParallelism = 1` caveat that at-least-once requires. The README notes that setting `AckAfterHandler = true` plus `MaxDegreeOfParallelism = 1` enables at-least-once delivery for production use — and explains the parallelism constraint (Kafka offset commits are monotonic; concurrent commits of out-of-order offsets could advance past in-flight messages and undo the guarantee).

### D13: NotificationService does NOT depend on order-service in compose

In the RMQ example the consumer depends on `order-service: service_started` because the consumer only *binds* the exchange (`DeclareExchange = false` on the consumer side) and would fail if the exchange does not exist yet. In Kafka there is no equivalent binding step — the consumer's `Subscribe` call auto-creates the topic when `KAFKA_AUTO_CREATE_TOPICS_ENABLE=true`, and `FromEarliest = true` (the default in `KafkaConsumerOptions`) makes the consumer replay from offset 0 once `order-service` later publishes. So the dependency is unnecessary and the consumer is allowed to start first.

**Alternative considered**: keep the dependency "for log readability". Rejected — it would teach the wrong mental model. Kafka consumers should be free to start independently of producers; that's a Kafka design property worth showing.

## Risks / Trade-offs

- **PostgreSQL schema migration on every startup** → `InitializeAsync` is idempotent; safe. Adds a few hundred milliseconds.
- **Both services reference library projects directly** → example fails to compile if the library has errors. Acceptable; no NuGet publish step needed.
- **KRaft Kafka container first-boot latency** → KRaft broker may take 5–15 s to become ready. Mitigated by a `healthcheck` on the `kafka` service that polls the broker API, and `condition: service_healthy` in both service `depends_on` blocks.
- **Topic auto-creation** → `KAFKA_AUTO_CREATE_TOPICS_ENABLE=true` creates `raytree.order-changes` with default partition count (1) and replication factor (1). Acceptable for a single-broker example; production deployments would pre-create topics with explicit settings.
- **Non-atomic repository + outbox writes** → See D8. Documented in README.
- **Default 5 s outbox polling sluggish for a demo** → Mitigated by setting `PollingInterval = TimeSpan.FromMilliseconds(500)` in `OrderService`.
- **Hard-coded localhost broker addresses** → Runtime behaviour configurable via `KAFKA_BOOTSTRAP_SERVERS` and `POSTGRES_CONNECTION` environment variables; README documents the pattern.
