## 1. Solution Scaffold

- [ ] 1.1 Create `examples/Kafka.Microservices/` directory structure
- [ ] 1.2 Create `Kafka.Microservices.slnx` solution file (standalone — not added to `RayTree.slnx`)
- [ ] 1.3 Add `Shared/Shared.csproj` class library project (target framework `net10.0`) — holds the `Order` entity
- [ ] 1.4 Add `OrderService/OrderService.csproj` console-app project referencing `Shared`, `RayTree.Core`, `RayTree.Hosting`, `RayTree.Plugins.PostgreSQL`, `RayTree.Plugins.Kafka`, `RayTree.Plugins.Serializers.MessagePack`, `RayTree.Plugins.Compressors.Gzip`
- [ ] 1.5 Add `NotificationService/NotificationService.csproj` console-app project referencing `Shared`, `RayTree.Core`, `RayTree.Hosting`, `RayTree.Plugins.Kafka`, `RayTree.Plugins.Serializers.MessagePack`, `RayTree.Plugins.Compressors.Gzip`
- [ ] 1.6 Add all three projects to the solution file
- [ ] 1.7 Create `examples/Kafka.Microservices/Directory.Build.props` that `<Import>`s the repo-root `Directory.Build.props` and overrides `<IsPackable>false</IsPackable>` plus blanks out packaging metadata that doesn't apply to console apps
- [ ] 1.8 Create `examples/Kafka.Microservices/Directory.Packages.props` that `<Import>`s the repo-root `Directory.Packages.props` and appends `<PackageVersion Include="Microsoft.Extensions.Hosting" Version="10.0.8" />`
- [ ] 1.9 Verify no `Version=` attributes appear on any `<PackageReference>` so central package management governs versions

## 2. Shared Entity

- [ ] 2.1 Create `Shared/Order.cs` with `[Table("orders")]` on the class and the following properties: `[Key] Guid Id`, `string CustomerName`, `decimal TotalAmount`, `string Status`
- [ ] 2.2 Ensure `Order` is a plain POCO — no `[MessagePackObject]` / `[Key(int)]` attributes are required because the plugin uses the contractless typeless resolver

## 3. OrderService Implementation

- [ ] 3.1 In `OrderService/Program.cs` use `Host.CreateApplicationBuilder(args)` and register RayTree via `builder.Services.AddChangeTracking(builder.Configuration, configure => { ... })`
- [ ] 3.2 Inside the `configure` callback: register `PostgreSqlOutbox<Order>` and `PostgreSqlRepository<Order>` against the connection string from `POSTGRES_CONNECTION` (default `Host=localhost;Port=5432;Database=raytree_example;Username=postgres;Password=postgres`)
- [ ] 3.3 Register `KafkaPublisher` targeting topic `raytree.order_changes`; use the default `KeySelector` (`envelope => $"{envelope.EntityType}:{envelope.EntityId}"`) — no explicit override required
- [ ] 3.4 Call `.UseMessagePackSerializer()` and `.UseGzipCompressor()` on the builder so the payload pipeline is MessagePack + Gzip
- [ ] 3.5 Configure `OutboxPublisherOptions.PollingInterval = TimeSpan.FromMilliseconds(500)` for snappy demo behaviour
- [ ] 3.6 Read Kafka broker address from `KAFKA_BOOTSTRAP_SERVERS` environment variable (default `localhost:9092`)
- [ ] 3.7 Add a `BackgroundService` (e.g. `OrderSimulator`) that periodically inserts, updates, and deletes `Order` rows via `IRepository<Order>` and tracks each change via `EntityChangeTracker.TrackXxxAsync`. Use a short delay between operations (e.g. 1–2 s) so output is readable.
- [ ] 3.8 Log every operation to the console using `ILogger<OrderSimulator>` (structured logging via Generic Host defaults)
- [ ] 3.9 Rely on `IHostApplicationLifetime` for graceful shutdown — no manual `Console.CancelKeyPress` wiring needed

## 4. NotificationService Implementation

- [ ] 4.1 In `NotificationService/Program.cs` use `Host.CreateApplicationBuilder(args)` and register RayTree via `builder.Services.AddChangeTracking(builder.Configuration, configure => { ... })`
- [ ] 4.2 Register a `KafkaConsumer` subscribed to topic `raytree.order_changes` with `GroupId = "notification-service"` and `BootstrapServers` from `KAFKA_BOOTSTRAP_SERVERS` (default `localhost:9092`); leave `FromEarliest = true` (the default in `KafkaConsumerOptions`) and `AckAfterHandler = false` (also the default) — at-most-once is the example baseline
- [ ] 4.3 Call `.UseMessagePackSerializer()` and `.UseGzipCompressor()` on the builder — must match `OrderService`'s payload pipeline exactly
- [ ] 4.4 Inside `ForEntity<Order>(b => b.UseConsumer(...))`, chain `OnInsert` / `OnUpdate` / `OnDelete` handlers in shared-handler mode that each log the change details with `ILogger`
- [ ] 4.5 Rely on `ChangeTrackingHostedService` (registered by `AddChangeTracking`) for `StartAsync`/`StopAsync` — no manual lifetime code

## 5. Docker Compose

- [ ] 5.1 Create `docker-compose.yml` with `postgres:18.1-alpine3.22` service on port 5432, env vars `POSTGRES_DB=raytree_example`, `POSTGRES_USER=postgres`, `POSTGRES_PASSWORD=postgres`, and a `healthcheck` running `pg_isready -U postgres -d raytree_example` every 5 s
- [ ] 5.2 Add a Kafka service using **`apache/kafka:3.9.0`** (pinned, not `:latest`) in KRaft single-node mode exposing port 9092. Required env vars: `KAFKA_AUTO_CREATE_TOPICS_ENABLE=true`, `KAFKA_NUM_PARTITIONS=3` (so the partition-key behaviour is observable — with 1 partition every message lands on the same partition regardless of key), `KAFKA_PROCESS_ROLES=broker,controller`, `KAFKA_NODE_ID=1`, `KAFKA_CONTROLLER_QUORUM_VOTERS=1@kafka:9093`, `KAFKA_LISTENERS=PLAINTEXT://0.0.0.0:9092,CONTROLLER://0.0.0.0:9093`, `KAFKA_ADVERTISED_LISTENERS=PLAINTEXT://kafka:9092`, `KAFKA_CONTROLLER_LISTENER_NAMES=CONTROLLER`, `KAFKA_INTER_BROKER_LISTENER_NAME=PLAINTEXT`, `KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR=1`, `KAFKA_GROUP_INITIAL_REBALANCE_DELAY_MS=0`
- [ ] 5.3 Configure Kafka healthcheck as a TCP probe (`test: ["CMD-SHELL", "nc -z localhost 9092 || exit 1"]`) with `interval: 10s`, `timeout: 5s`, `retries: 10`, and `start_period: 60s` — accommodates KRaft controller-election latency on cold start without depending on `kafka-topics.sh` or other JVM-bound tooling
- [ ] 5.4 Add `order-service` with env `KAFKA_BOOTSTRAP_SERVERS=kafka:9092`, `POSTGRES_CONNECTION=Host=postgres;Port=5432;Database=raytree_example;Username=postgres;Password=postgres`, and `depends_on: {postgres: {condition: service_healthy}, kafka: {condition: service_healthy}}`
- [ ] 5.5 Add `notification-service` with env `KAFKA_BOOTSTRAP_SERVERS=kafka:9092` and `depends_on: {kafka: {condition: service_healthy}}` — **no dependency on `order-service`** because Kafka auto-creates the topic on the consumer's `Subscribe` call and the `FromEarliest = true` default makes the consumer replay from offset 0 once `order-service` later publishes (per design D13)
- [ ] 5.6 Define a named volume for PostgreSQL data so restarts preserve the outbox state

## 6. Dockerfiles

- [ ] 6.1 Add multi-stage `OrderService/Dockerfile` (`mcr.microsoft.com/dotnet/sdk:10.0` build → `mcr.microsoft.com/dotnet/runtime:10.0` runtime)
- [ ] 6.2 Add multi-stage `NotificationService/Dockerfile` with the same base images
- [ ] 6.3 Ensure both Dockerfiles copy and restore from the repo root so `ProjectReference` paths to `src/RayTree.*` resolve correctly inside the build context

## 7. Documentation

- [ ] 7.1 Write `README.md` covering: prerequisites (Docker, .NET 10 SDK for local dev), `docker compose up` instructions, expected console output, Kafka broker address (`localhost:9092`), PostgreSQL connection details, project structure overview
- [ ] 7.2 Add a **"Known limitations"** section explaining the non-atomic repository + outbox writes (per design decision D8) and pointing readers to `RayTree.EntityFrameworkCore` / `EntityChangeInterceptor` as the production-grade transactional path
- [ ] 7.3 Add a **"Going further"** section covering: NOTIFY/LISTEN fast-path (`PostgreSqlOutboxOptions.UseNotificationChannel = true`); **at-least-once delivery** — set `KafkaConsumerOptions.AckAfterHandler = true` AND `SubscriberOptions.MaxDegreeOfParallelism = 1` together (explain explicitly that Kafka offset commits are monotonic and concurrent commits of out-of-order offsets would advance past in-flight messages, undoing the guarantee); custom `KeySelector` for sharding by tenant or aggregate root; isolated-handler dispatch mode for per-handler consumer groups
- [ ] 7.4 Add a **"Consumer-group scaling"** section showing that running multiple `NotificationService` replicas with the same `GroupId` causes Kafka to rebalance the 3 partitions across them automatically — demonstrate by running `docker compose up --scale notification-service=2` and observing partition assignment in the logs
- [ ] 7.5 Add a **"Partition-key behaviour"** section showing how to inspect partition assignment (`docker exec kafka /opt/kafka/bin/kafka-console-consumer.sh --topic raytree.order_changes --partition <N> --bootstrap-server localhost:9092 --from-beginning`) and verify that all events for one `Order.Id` land on the same partition while different `Id`s spread across partitions
- [ ] 7.6 Document the **`FromEarliest = true`** default — a new consumer group reads from offset 0; restarting the same group resumes from the last committed offset. This is why `notification-service` can start before `order-service` and still see every message.
- [ ] 7.7 Note in README that the example is intentionally not part of `RayTree.slnx` — open `examples/Kafka.Microservices/Kafka.Microservices.slnx` directly
- [ ] 7.8 Add inline code comments in both `Program.cs` files explaining the key builder calls (one short line per non-obvious step)
