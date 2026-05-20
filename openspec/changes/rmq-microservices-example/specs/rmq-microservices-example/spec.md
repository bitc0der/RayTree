## ADDED Requirements

### Requirement: Example solution structure
The example SHALL be a standalone .NET solution at `examples/RabbitMQ.Microservices/` containing two console-app projects (`OrderService` and `NotificationService`) and a shared `docker-compose.yml`. It SHALL NOT be included in the main `RayTree.slnx` solution.

#### Scenario: Solution compiles independently
- **WHEN** a developer runs `dotnet build` inside `examples/RabbitMQ.Microservices/`
- **THEN** both `OrderService` and `NotificationService` build successfully without errors

#### Scenario: Projects reference library via ProjectReference
- **WHEN** the example projects are opened in an IDE
- **THEN** `OrderService` and `NotificationService` reference RayTree packages via `<ProjectReference>` pointing to the `src/` directory

### Requirement: Order entity definition
Both services SHALL share a common `Order` entity class with at least `Id` (Guid, annotated `[Key]`), `CustomerName` (string), `TotalAmount` (decimal), and `Status` (string) properties. The entity SHALL be placed in a shared `Shared/` folder or project accessible to both services. The `[Key]` annotation is required so `PostgreSqlRepository<Order>` can derive the primary key at construction time.

#### Scenario: Entity properties are serializable
- **WHEN** an `Order` is serialized by `RayTree.Plugins.Serializers.Json`
- **THEN** all four properties round-trip correctly through JSON serialization

### Requirement: OrderService publishes change events
`OrderService` SHALL configure `EntityChangeTracker` with a `PostgreSqlOutbox<Order>`, a `PostgreSqlRepository<Order>`, and a `RabbitMqPublisher` targeting a topic exchange named `raytree.changes`. It SHALL periodically create, update, and delete `Order` entities — writing through the repository and tracking changes — to generate a continuous stream of change events.

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
`NotificationService` SHALL configure `EntityChangeTracker` with a `RabbitMqConsumer` bound to the `raytree.changes` exchange with routing key `change.Order.*`. It SHALL register separate handlers for Insert, Update, and Delete change types using shared-handler dispatch mode.

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
`OrderService` SHALL use `PostgreSqlOutbox<Order>` and `PostgreSqlRepository<Order>`. On startup, `InitializeAsync` SHALL automatically create or migrate the `order_outbox` and `orders` tables. The `Order` entity SHALL carry a `[Key]` attribute on `Id` so `PostgreSqlRepository` can derive the primary key without convention fallback.

#### Scenario: Schema created on first run
- **WHEN** `OrderService` starts against an empty PostgreSQL database
- **THEN** `order_outbox` and `orders` tables are created with all required columns and indexes before the publish loop starts

#### Scenario: Schema migration is idempotent
- **WHEN** `OrderService` is restarted against a database that already has the tables
- **THEN** startup completes without error and no duplicate tables or columns are created

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

### Requirement: README with run instructions
A `README.md` SHALL accompany the example explaining prerequisites, how to run with Docker Compose, what to observe in the console output and RabbitMQ management UI, and a brief description of the project structure.

#### Scenario: Developer can follow README without prior RayTree knowledge
- **WHEN** a developer reads the README and follows its steps
- **THEN** they can run the example and see change events flowing from `OrderService` to `NotificationService` in under 5 minutes
