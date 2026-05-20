## ADDED Requirements

### Requirement: Example solution structure
The example SHALL be a standalone .NET solution at `examples/RabbitMQ.Microservices/` containing three projects (a `Shared` class library, an `OrderService` console app, and a `NotificationService` console app) plus a `docker-compose.yml`. It SHALL NOT be included in the main `RayTree.slnx` solution.

#### Scenario: Solution compiles independently
- **WHEN** a developer runs `dotnet build` inside `examples/RabbitMQ.Microservices/`
- **THEN** `Shared`, `OrderService`, and `NotificationService` build successfully without errors

#### Scenario: Projects reference library via ProjectReference
- **WHEN** the example projects are opened in an IDE
- **THEN** `OrderService` and `NotificationService` reference RayTree assemblies via `<ProjectReference>` pointing to the `src/` directory, and both reference the `Shared` project for the `Order` entity

#### Scenario: Central package management is inherited
- **WHEN** a developer inspects the example `.csproj` files
- **THEN** package references carry no `Version=` attribute and resolve via the parent `Directory.Packages.props`

### Requirement: Order entity definition
Both services SHALL share a common `Order` entity class with at least `Id` (Guid, explicitly annotated `[Key]`), `CustomerName` (string), `TotalAmount` (decimal), and `Status` (string) properties. The class SHALL carry `[Table("orders")]` so the `PostgreSqlRepository<Order>` source table is the plural form. The entity SHALL live in a `Shared` class library project referenced by both services.

The `[Key]` annotation is used here for explicitness — the library's `EntityColumnMapper` would also fall back to the `Id` convention property, but the example favours an unambiguous declaration to teach the recommended pattern.

#### Scenario: Entity properties round-trip through MessagePack
- **WHEN** an `Order` is serialized by `RayTree.Plugins.Serializers.MessagePack` and then deserialized
- **THEN** all four properties (`Id`, `CustomerName`, `TotalAmount`, `Status`) round-trip with their original values

#### Scenario: No MessagePack-specific attributes required
- **WHEN** the `Order` POCO is inspected
- **THEN** it carries no `[MessagePackObject]` / `[Key(int)]` attributes — the contractless resolver handles plain properties

### Requirement: OrderService publishes change events
`OrderService` SHALL configure `EntityChangeTracker` with a `PostgreSqlOutbox<Order>`, a `PostgreSqlRepository<Order>`, a `RabbitMqPublisher` targeting a topic exchange named `raytree.changes`, the MessagePack serializer (`.UseMessagePackSerializer()`), and the Gzip compressor (`.UseGzipCompressor()`). It SHALL periodically create, update, and delete `Order` entities — writing through the repository and tracking changes — to generate a continuous stream of change events.

#### Scenario: Insert event published
- **WHEN** `OrderService` calls `TrackInsertAsync` for a new `Order`
- **THEN** a `MessageEnvelope` with `ChangeType = Insert` and routing key `change.Order.insert` is published to the `raytree.changes` exchange

#### Scenario: Update event published
- **WHEN** `OrderService` calls `TrackUpdateAsync` for an existing `Order`
- **THEN** a `MessageEnvelope` with `ChangeType = Update` and routing key `change.Order.update` is published to the `raytree.changes` exchange

#### Scenario: Delete event published
- **WHEN** `OrderService` calls `TrackDeleteAsync` for an `Order`
- **THEN** a `MessageEnvelope` with `ChangeType = Delete` and routing key `change.Order.delete` is published to the `raytree.changes` exchange

### Requirement: NotificationService consumes change events
`NotificationService` SHALL configure `EntityChangeTracker` with a `RabbitMqConsumer` bound to the `raytree.changes` exchange via a queue named `notification-service.orders` with routing key `change.Order.*`, plus the matching MessagePack serializer (`.UseMessagePackSerializer()`) and Gzip compressor (`.UseGzipCompressor()`) so payloads decode correctly. The queue SHALL be declared `durable: true, exclusive: false, autoDelete: false` so it survives broker restarts and supports horizontal scaling of consumer replicas. It SHALL register separate handlers for Insert, Update, and Delete change types using shared-handler dispatch mode.

#### Scenario: Serializer / compressor mismatch fails fast
- **WHEN** `NotificationService` is started with a different serializer or compressor than `OrderService`
- **THEN** deserialization throws on the first received envelope and the consumer logs the error (illustrating that both services must register the same payload pipeline)

#### Scenario: Insert handler invoked
- **WHEN** a `MessageEnvelope` with `ChangeType = Insert` arrives at `NotificationService`
- **THEN** the registered `OnInsert` handler is invoked with the deserialized `EntityChange<Order>`

#### Scenario: Update handler invoked
- **WHEN** a `MessageEnvelope` with `ChangeType = Update` arrives at `NotificationService`
- **THEN** the registered `OnUpdate` handler is invoked with the deserialized `EntityChange<Order>`

#### Scenario: Delete handler invoked
- **WHEN** a `MessageEnvelope` with `ChangeType = Delete` arrives at `NotificationService`
- **THEN** the registered `OnDelete` handler is invoked with the deserialized `EntityChange<Order>`

### Requirement: PostgreSQL outbox and repository schema
`OrderService` SHALL use `PostgreSqlOutbox<Order>` and `PostgreSqlRepository<Order>`. On startup, `InitializeAsync` SHALL automatically create or migrate the `order_outbox` table (default outbox naming convention) and the `orders` table (set by `[Table("orders")]` on the entity).

#### Scenario: Schema created on first run
- **WHEN** `OrderService` starts against an empty PostgreSQL database
- **THEN** `order_outbox` and `orders` tables are created with all required columns and indexes before the publish loop starts

#### Scenario: Schema migration is idempotent
- **WHEN** `OrderService` is restarted against a database that already has the tables
- **THEN** startup completes without error and no duplicate tables or columns are created

### Requirement: Outbox polling interval
`OrderService` SHALL configure `OutboxPublisherOptions.PollingInterval = TimeSpan.FromMilliseconds(500)` so demonstration changes surface in RabbitMQ within roughly half a second of being tracked, rather than the default 5 s.

#### Scenario: Demo events appear quickly
- **WHEN** `OrderService` calls `TrackInsertAsync`
- **THEN** the corresponding `MessageEnvelope` is observable in RabbitMQ within ~1 s under steady-state polling

### Requirement: Docker Compose local run
A `docker-compose.yml` at the example root SHALL define services for PostgreSQL, RabbitMQ (with management plugin), `order-service`, and `notification-service`. Running `docker compose up` SHALL start all four services without manual configuration steps. The `order-service` SHALL declare a `depends_on` health-check dependency on the `postgres` service so it does not start before the database is ready.

#### Scenario: Services start with docker compose up
- **WHEN** a developer runs `docker compose up` from `examples/RabbitMQ.Microservices/`
- **THEN** PostgreSQL starts on port 5432, RabbitMQ starts on ports 5672 and 15672, and both .NET services connect and begin producing/consuming messages

#### Scenario: RabbitMQ connection is configurable
- **WHEN** a developer sets the `RABBITMQ_HOST` environment variable before running
- **THEN** both services use that host instead of the default `localhost`

#### Scenario: PostgreSQL connection is configurable
- **WHEN** a developer sets the `POSTGRES_CONNECTION` environment variable before running
- **THEN** `OrderService` uses that connection string instead of the default pointing to `localhost`

### Requirement: Generic Host integration and graceful shutdown
Both services SHALL bootstrap via `Host.CreateApplicationBuilder(args)` and register RayTree through `services.AddChangeTracking(configuration, configure)` from `RayTree.Hosting`. They SHALL NOT use the raw `ChangeTrackingBuilder` directly. The `IHostApplicationLifetime` SHALL drive graceful shutdown — Ctrl+C, SIGTERM, or `docker compose down` SHALL stop the publisher and consumer loops cleanly without dropping in-flight messages where the broker supports acknowledgement.

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
- **THEN** a clearly marked section titled "Known limitations" (or equivalent) explains the non-atomic write path and recommends EF Core integration for production use

### Requirement: README with run instructions
A `README.md` SHALL accompany the example explaining prerequisites, how to run with Docker Compose, what to observe in the console output and RabbitMQ management UI, and a brief description of the project structure.

#### Scenario: Developer can follow README without prior RayTree knowledge
- **WHEN** a developer reads the README and follows its steps
- **THEN** they can run the example and see change events flowing from `OrderService` to `NotificationService` in under 5 minutes
