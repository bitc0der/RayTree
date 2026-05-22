## ADDED Requirements

### Requirement: Example solution structure
The example SHALL be a standalone .NET solution at `examples/Kafka.Microservices/` containing three projects (a `Shared` class library, an `OrderService` console app, and a `NotificationService` console app) plus a `docker-compose.yml`. It SHALL NOT be included in the main `RayTree.slnx` solution.

#### Scenario: Solution compiles independently
- **WHEN** a developer runs `dotnet build` inside `examples/Kafka.Microservices/`
- **THEN** `Shared`, `OrderService`, and `NotificationService` build successfully without errors

#### Scenario: Projects reference library via ProjectReference
- **WHEN** the example projects are opened in an IDE
- **THEN** `OrderService` and `NotificationService` reference RayTree assemblies via `<ProjectReference>` pointing to the `src/` directory, and both reference the `Shared` project for the `Order` entity

#### Scenario: Central package management is inherited via local props files
- **WHEN** a developer inspects the example `.csproj` files
- **THEN** package references carry no `Version=` attribute and resolve via `examples/Kafka.Microservices/Directory.Packages.props`, which itself `<Import>`s the repo-root `Directory.Packages.props` and only appends example-only packages (e.g. `Microsoft.Extensions.Hosting`)

#### Scenario: Local Directory.Build.props isolates packaging metadata
- **WHEN** a developer inspects `examples/Kafka.Microservices/Directory.Build.props`
- **THEN** it imports the repo-root `Directory.Build.props` and overrides packaging metadata (`<IsPackable>false</IsPackable>`, no `<VersionPrefix>` / author / license inheritance for the console apps), leaving the root file untouched

### Requirement: Order entity definition
Both services SHALL share a common `Order` entity class with at least `Id` (Guid, explicitly annotated `[Key]`), `CustomerName` (string), `TotalAmount` (decimal), and `Status` (string) properties. The class SHALL carry `[Table("orders")]` so the `PostgreSqlRepository<Order>` source table is the plural form. The entity SHALL live in a `Shared` class library project referenced by both services.

#### Scenario: Entity properties round-trip through MessagePack
- **WHEN** an `Order` is serialized by `RayTree.Plugins.Serializers.MessagePack` and then deserialized
- **THEN** all four properties (`Id`, `CustomerName`, `TotalAmount`, `Status`) round-trip with their original values

#### Scenario: No MessagePack-specific attributes required
- **WHEN** the `Order` POCO is inspected
- **THEN** it carries no `[MessagePackObject]` / `[Key(int)]` attributes — the contractless resolver handles plain properties

### Requirement: OrderService publishes change events
`OrderService` SHALL configure `EntityChangeTracker` with a `PostgreSqlOutbox<Order>`, a `PostgreSqlRepository<Order>`, a `KafkaPublisher` targeting a topic named `raytree.order_changes`, the MessagePack serializer (`.UseMessagePackSerializer()`), and the Gzip compressor (`.UseGzipCompressor()`). It SHALL periodically create, update, and delete `Order` entities — writing through the repository and tracking changes — to generate a continuous stream of change events.

#### Scenario: Insert event published
- **WHEN** `OrderService` calls `TrackInsertAsync` for a new `Order`
- **THEN** a `MessageEnvelope` with `ChangeType = Insert` is published to the `raytree.order_changes` Kafka topic with a partition key of `"Order:<orderId>"`

#### Scenario: Update event published
- **WHEN** `OrderService` calls `TrackUpdateAsync` for an existing `Order`
- **THEN** a `MessageEnvelope` with `ChangeType = Update` is published to the `raytree.order_changes` topic with the same partition key, guaranteeing the update lands on the same partition as the preceding Insert

#### Scenario: Delete event published
- **WHEN** `OrderService` calls `TrackDeleteAsync` for an `Order`
- **THEN** a `MessageEnvelope` with `ChangeType = Delete` is published to the `raytree.order_changes` topic on the same partition as the Insert and Update for that entity

### Requirement: NotificationService consumes change events
`NotificationService` SHALL configure `EntityChangeTracker` with a `KafkaConsumer` subscribed to the `raytree.order_changes` topic using `GroupId = "notification-service"`, plus the matching MessagePack serializer (`.UseMessagePackSerializer()`) and Gzip compressor (`.UseGzipCompressor()`). It SHALL register separate handlers for Insert, Update, and Delete change types using shared-handler dispatch mode.

#### Scenario: Insert handler invoked
- **WHEN** a `MessageEnvelope` with `ChangeType = Insert` is consumed from Kafka
- **THEN** the registered `OnInsert` handler is invoked with the deserialized `EntityChange<Order>`

#### Scenario: Update handler invoked
- **WHEN** a `MessageEnvelope` with `ChangeType = Update` is consumed from Kafka
- **THEN** the registered `OnUpdate` handler is invoked with the deserialized `EntityChange<Order>`

#### Scenario: Delete handler invoked
- **WHEN** a `MessageEnvelope` with `ChangeType = Delete` is consumed from Kafka
- **THEN** the registered `OnDelete` handler is invoked with the deserialized `EntityChange<Order>`

#### Scenario: Serializer / compressor mismatch fails fast
- **WHEN** `NotificationService` is started with a different serializer or compressor than `OrderService`
- **THEN** deserialization throws on the first received envelope (illustrating that both services must register the same payload pipeline)

### Requirement: Default partition key preserves per-entity ordering
`OrderService` SHALL use the default `KafkaPublisherOptions.KeySelector` (`envelope => $"{envelope.EntityType}:{envelope.EntityId}"`). All changes for the same `Order` entity SHALL be published to the same Kafka partition, so `NotificationService` processes Insert / Update / Delete events for a given entity in the order they were written. The example topic SHALL be created with **at least 3 partitions** so the partition-key effect is observable (with 1 partition every message lands on the same partition regardless of key).

#### Scenario: Same entity lands on same partition
- **WHEN** Insert, Update, and Delete events are published for the same `Order.Id` against a multi-partition topic
- **THEN** all three `DeliveryResult` records returned by `Confluent.Kafka` report the same `Partition` value

#### Scenario: Different entities spread across partitions
- **WHEN** Insert events are published for several different `Order.Id` values against a multi-partition topic
- **THEN** the resulting partition assignments are distributed (not all messages on partition 0), demonstrating that the default key selector shards by entity

### Requirement: Default at-most-once delivery
The example SHALL use the default `KafkaConsumerOptions.AckAfterHandler = false`. The offset is committed on the poll thread immediately after parsing the message, before dispatch to `ChangeSubscriber`. The README SHALL document this and SHALL explain how to opt in to at-least-once delivery (`AckAfterHandler = true`) and the accompanying `MaxDegreeOfParallelism = 1` requirement.

#### Scenario: Default is at-most-once
- **WHEN** the `KafkaConsumer` is constructed in `NotificationService`
- **THEN** `KafkaConsumerOptions.AckAfterHandler` is left at its default `false`, and no explicit `MaxDegreeOfParallelism` override is set on `SubscriberOptions`

### Requirement: Consumer group enables horizontal scaling
The `NotificationService` `GroupId` SHALL be a stable identifier (`"notification-service"`) so that multiple replicas of `NotificationService` form a single Kafka consumer group. Kafka SHALL assign disjoint subsets of the topic's partitions to each replica, parallelising consumption without any RayTree-level configuration.

#### Scenario: Two replicas share the partitions
- **WHEN** a second instance of `NotificationService` is started with the same `GroupId` against a topic with at least 2 partitions
- **THEN** the Kafka broker triggers a consumer-group rebalance and each replica receives a non-overlapping subset of the partitions

### Requirement: PostgreSQL outbox and repository schema
`OrderService` SHALL use `PostgreSqlOutbox<Order>` and `PostgreSqlRepository<Order>`. On startup, `InitializeAsync` SHALL automatically create or migrate the `order_outbox` table and the `orders` table (set by `[Table("orders")]` on the entity).

#### Scenario: Schema created on first run
- **WHEN** `OrderService` starts against an empty PostgreSQL database
- **THEN** `order_outbox` and `orders` tables are created with all required columns and indexes before the publish loop starts

#### Scenario: Schema migration is idempotent
- **WHEN** `OrderService` is restarted against a database that already has the tables
- **THEN** startup completes without error and no duplicate tables or columns are created

### Requirement: Outbox polling interval
`OrderService` SHALL configure `OutboxPublisherOptions.PollingInterval = TimeSpan.FromMilliseconds(500)` so demonstration changes surface in Kafka within roughly half a second of being tracked, rather than the default 5 s.

#### Scenario: Demo events appear quickly
- **WHEN** `OrderService` calls `TrackInsertAsync`
- **THEN** the corresponding `MessageEnvelope` is observable in Kafka within ~1 s under steady-state polling

### Requirement: Docker Compose local run
A `docker-compose.yml` at the example root SHALL define services for PostgreSQL, Kafka (KRaft mode, no Zookeeper), `order-service`, and `notification-service`. Running `docker compose up` SHALL start all services without manual configuration steps. The Kafka service SHALL pin a concrete image tag (e.g. `apache/kafka:3.9.0`), not `:latest`. The `order-service` SHALL declare `depends_on` health-check dependencies on both `postgres` and `kafka`. The `notification-service` SHALL declare only a health-check dependency on `kafka` — it does NOT depend on `order-service` because Kafka auto-creates the topic on the first `Subscribe` call, so the consumer can start before the producer without losing messages (the `FromEarliest = true` consumer default replays from offset 0 when `order-service` later begins publishing).

The Kafka healthcheck SHALL use a TCP-level probe (`nc -z localhost 9092` or equivalent) with a `start_period` of at least 60 seconds to accommodate KRaft controller election latency on cold start. The healthcheck SHALL NOT depend on `kafka-topics.sh` or other JVM-startup-bound tooling.

#### Scenario: Services start with docker compose up
- **WHEN** a developer runs `docker compose up` from `examples/Kafka.Microservices/`
- **THEN** PostgreSQL starts on port 5432, Kafka starts on port 9092, and both .NET services connect and begin producing/consuming messages

#### Scenario: Kafka broker address is configurable
- **WHEN** a developer sets the `KAFKA_BOOTSTRAP_SERVERS` environment variable before running
- **THEN** both services use that address instead of the default `localhost:9092`

#### Scenario: PostgreSQL connection is configurable
- **WHEN** a developer sets the `POSTGRES_CONNECTION` environment variable before running
- **THEN** `OrderService` uses that connection string instead of the default

### Requirement: Topic auto-creation with multiple partitions
The `docker-compose.yml` SHALL configure the Kafka broker with `KAFKA_AUTO_CREATE_TOPICS_ENABLE=true` AND `KAFKA_NUM_PARTITIONS=3` (or higher) so the `raytree.order_changes` topic is created automatically on the first produce or subscribe call with enough partitions to demonstrate per-entity routing. No manual topic administration step SHALL be required.

#### Scenario: Topic created on first publish
- **WHEN** `OrderService` publishes its first message to `raytree.order_changes`
- **THEN** the broker creates the topic automatically with 3 partitions

#### Scenario: Topic created on first subscribe
- **WHEN** `NotificationService` starts before `OrderService` and calls `Subscribe`
- **THEN** the broker creates the topic automatically with 3 partitions, and `NotificationService` begins receiving messages as soon as `OrderService` publishes

### Requirement: Generic Host integration and graceful shutdown
Both services SHALL bootstrap via `Host.CreateApplicationBuilder(args)` and register RayTree through `services.AddChangeTracking(configuration, configure)` from `RayTree.Hosting`. The `IHostApplicationLifetime` SHALL drive graceful shutdown — Ctrl+C, SIGTERM, or `docker compose down` SHALL stop the publisher and consumer loops cleanly.

#### Scenario: Ctrl+C triggers graceful shutdown
- **WHEN** a developer presses Ctrl+C in the `OrderService` console
- **THEN** `OutboxPublisherService` stops polling, in-flight publishes complete, and the process exits with code 0

#### Scenario: docker compose down stops cleanly
- **WHEN** a developer runs `docker compose down`
- **THEN** both services receive SIGTERM, `ChangeTrackingHostedService.StopAsync` runs, and containers exit gracefully

### Requirement: Atomicity caveat is documented
Because `PostgreSqlRepository<Order>` and `PostgreSqlOutbox<Order>` do not share a transaction in this example, the README SHALL contain a prominent caveat explaining that a crash between the repository write and the outbox write can leave the two tables inconsistent, and SHALL point readers to `RayTree.EntityFrameworkCore` (`EntityChangeInterceptor`) as the production-grade transactional path.

#### Scenario: Caveat is visible in README
- **WHEN** a developer reads the example README
- **THEN** a clearly marked section explains the non-atomic write path and recommends EF Core integration for production use

### Requirement: README with run instructions
A `README.md` SHALL accompany the example explaining prerequisites, how to run with Docker Compose, what to observe in the console output, and a brief description of the project structure. It SHALL cover the following Kafka-specific topics:

- **Default partition-key strategy** — `EntityType:EntityId` keeps per-entity ordering; how to override `KafkaPublisherOptions.KeySelector` to shard by tenant or aggregate root.
- **`FromEarliest = true` default** — a new consumer group reads from offset 0; restarting the same group resumes from the last committed offset.
- **Delivery guarantees** — the example uses the default `AckAfterHandler = false` (at-most-once); document how to switch to at-least-once (`AckAfterHandler = true`) AND the accompanying `SubscriberOptions.MaxDegreeOfParallelism = 1` requirement (Kafka offset commits are monotonic — concurrent commits could skip in-flight messages).
- **Consumer-group scaling** — running multiple `NotificationService` replicas with the same `GroupId` causes Kafka to rebalance partitions across them automatically, with no RayTree-level configuration.

#### Scenario: Developer can follow README without prior RayTree knowledge
- **WHEN** a developer reads the README and follows its steps
- **THEN** they can run the example and see change events flowing from `OrderService` to `NotificationService` in under 5 minutes

#### Scenario: README documents at-least-once trade-off
- **WHEN** a developer reads the delivery-guarantees section
- **THEN** they see both the flag (`AckAfterHandler = true`) and the parallelism constraint (`MaxDegreeOfParallelism = 1`) called out explicitly
