## 1. Solution Scaffold

- [ ] 1.1 Create `examples/RabbitMQ.Microservices/` directory structure
- [ ] 1.2 Create `RabbitMQ.Microservices.slnx` solution file (standalone — not added to `RayTree.slnx`)
- [ ] 1.3 Add `Shared/Shared.csproj` class library project (target framework `net10.0`) — holds the `Order` entity
- [ ] 1.4 Add `OrderService/OrderService.csproj` console-app project referencing `Shared`, `RayTree.Core`, `RayTree.Hosting`, `RayTree.Plugins.PostgreSQL`, `RayTree.Plugins.RabbitMQ`, `RayTree.Plugins.Serializers.MessagePack`, `RayTree.Plugins.Compressors.Gzip`
- [ ] 1.5 Add `NotificationService/NotificationService.csproj` console-app project referencing `Shared`, `RayTree.Core`, `RayTree.Hosting`, `RayTree.Plugins.RabbitMQ`, `RayTree.Plugins.Serializers.MessagePack`, `RayTree.Plugins.Compressors.Gzip`
- [ ] 1.6 Add all three projects to the solution file
- [ ] 1.7 Verify no `Version=` attributes appear on any `<PackageReference>` so the parent `Directory.Packages.props` governs versions

## 2. Shared Entity

- [ ] 2.1 Create `Shared/Order.cs` with `[Table("orders")]` on the class and the following properties: `[Key] Guid Id`, `string CustomerName`, `decimal TotalAmount`, `string Status`
- [ ] 2.2 Ensure `Order` is a plain POCO (no behaviour) — no `[MessagePackObject]` / `[Key(int)]` MessagePack attributes are required because the plugin uses the contractless typeless resolver

## 3. OrderService Implementation

- [ ] 3.1 In `OrderService/Program.cs` use `Host.CreateApplicationBuilder(args)` and register RayTree via `builder.Services.AddChangeTracking(builder.Configuration, configure => { ... })`
- [ ] 3.2 Inside the `configure` callback: register `PostgreSqlOutbox<Order>` and `PostgreSqlRepository<Order>` against the connection string from `POSTGRES_CONNECTION` (default `Host=localhost;Port=5432;Database=raytree_example;Username=postgres;Password=postgres`)
- [ ] 3.3 Register `RabbitMqPublisher` targeting topic exchange `raytree.changes`; use the default `RoutingKey` (`"change"`) — no explicit override required
- [ ] 3.4 Call `.UseMessagePackSerializer()` and `.UseGzipCompressor()` on the builder so the payload pipeline is MessagePack + Gzip
- [ ] 3.5 Configure `OutboxPublisherOptions.PollingInterval = TimeSpan.FromMilliseconds(500)` for snappy demo behaviour
- [ ] 3.6 Read RabbitMQ host from `RABBITMQ_HOST` environment variable (default `localhost`)
- [ ] 3.7 Add a `BackgroundService` (e.g. `OrderSimulator`) that periodically inserts, updates, and deletes `Order` rows via `IRepository<Order>` and tracks each change via `EntityChangeTracker.TrackXxxAsync`. Use a short delay between operations (e.g. 1–2 s) so output is readable.
- [ ] 3.8 Log every operation to the console using `ILogger<OrderSimulator>` (structured logging via Generic Host defaults)
- [ ] 3.9 Rely on `IHostApplicationLifetime` for graceful shutdown — no manual `Console.CancelKeyPress` wiring needed

## 4. NotificationService Implementation

- [ ] 4.1 In `NotificationService/Program.cs` use `Host.CreateApplicationBuilder(args)` and register RayTree via `builder.Services.AddChangeTracking(builder.Configuration, configure => { ... })`
- [ ] 4.2 Register a `RabbitMqConsumer` bound to exchange `raytree.changes`, queue `notification-service.orders` (`durable: true, exclusive: false, autoDelete: false`), routing key `change.Order.*`
- [ ] 4.3 Call `.UseMessagePackSerializer()` and `.UseGzipCompressor()` on the builder — must match `OrderService`'s payload pipeline exactly or deserialization will fail
- [ ] 4.4 Inside `ForEntity<Order>(b => b.UseConsumer(...))`, register `OnInsert` / `OnUpdate` / `OnDelete` handlers in shared-handler mode that each log the change details with `ILogger`
- [ ] 4.5 Read RabbitMQ host from `RABBITMQ_HOST` environment variable (default `localhost`)
- [ ] 4.6 Rely on `ChangeTrackingHostedService` (registered by `AddChangeTracking`) for `StartAsync`/`StopAsync` — no manual lifetime code

## 5. Docker Compose

- [ ] 5.1 Create `docker-compose.yml` with `postgres:16-alpine` service on port 5432, env vars `POSTGRES_DB=raytree_example`, `POSTGRES_USER=postgres`, `POSTGRES_PASSWORD=postgres`, and `healthcheck` running `pg_isready -U postgres -d raytree_example` every 5 s
- [ ] 5.2 Add `rabbitmq:3-management-alpine` service exposing ports 5672 and 15672
- [ ] 5.3 Add `order-service` with env `RABBITMQ_HOST=rabbitmq`, `POSTGRES_CONNECTION=Host=postgres;Port=5432;Database=raytree_example;Username=postgres;Password=postgres`, and `depends_on: {postgres: {condition: service_healthy}, rabbitmq: {condition: service_started}}`
- [ ] 5.4 Add `notification-service` with env `RABBITMQ_HOST=rabbitmq` and `depends_on: {rabbitmq: {condition: service_started}}`
- [ ] 5.5 Define a named volume for PostgreSQL data so restarts preserve the outbox state

## 6. Dockerfiles

- [ ] 6.1 Add multi-stage `OrderService/Dockerfile` (`mcr.microsoft.com/dotnet/sdk:10.0` build → `mcr.microsoft.com/dotnet/runtime:10.0` runtime)
- [ ] 6.2 Add multi-stage `NotificationService/Dockerfile` with the same base images
- [ ] 6.3 Ensure both Dockerfiles copy and restore from the repo root so `ProjectReference` paths to `src/RayTree.*` resolve correctly inside the build context

## 7. Documentation

- [ ] 7.1 Write `README.md` covering: prerequisites (Docker, .NET 10 SDK for local dev), `docker compose up` instructions, expected console output, RabbitMQ management UI URL (`http://localhost:15672`, default `guest`/`guest`), PostgreSQL connection details, project structure overview
- [ ] 7.2 Add a **"Known limitations"** section to the README explaining the non-atomic repository + outbox writes (per design decision D8) and pointing readers to `RayTree.EntityFrameworkCore` / `EntityChangeInterceptor` as the production-grade transactional path
- [ ] 7.3 Add a **"Going further"** section mentioning: NOTIFY/LISTEN fast-path (`PostgreSqlOutboxOptions.UseNotificationChannel = true`), `AckAfterHandler` for at-least-once delivery, and isolated-handler dispatch mode for per-handler subscriptions
- [ ] 7.4 Note in README that the example is intentionally *not* part of `RayTree.slnx` — open `examples/RabbitMQ.Microservices/RabbitMQ.Microservices.slnx` directly
- [ ] 7.5 Add inline code comments in both `Program.cs` files explaining the key builder calls (one short line per non-obvious step)
