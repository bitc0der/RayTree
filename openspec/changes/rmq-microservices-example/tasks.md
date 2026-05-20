## 1. Solution Scaffold

- [ ] 1.1 Create `examples/RabbitMQ.Microservices/` directory structure
- [ ] 1.2 Create `RabbitMQ.Microservices.slnx` solution file
- [ ] 1.3 Add `OrderService/OrderService.csproj` console-app project referencing `RayTree.Core`, `RayTree.Plugins.PostgreSQL`, `RayTree.Plugins.RabbitMQ`, `RayTree.Plugins.Serializers.Json`
- [ ] 1.4 Add `NotificationService/NotificationService.csproj` console-app project referencing `RayTree.Core`, `RayTree.Plugins.RabbitMQ`, `RayTree.Plugins.Serializers.Json`
- [ ] 1.5 Add both projects to the solution file

## 2. Shared Entity

- [ ] 2.1 Create `Shared/Order.cs` with `[Key] Id` (Guid), `CustomerName` (string), `TotalAmount` (decimal), `Status` (string) properties
- [ ] 2.2 Reference `Shared/Order.cs` in both `OrderService` and `NotificationService` via a shared class library project or linked file

## 3. OrderService Implementation

- [ ] 3.1 Configure `ChangeTrackingBuilder` in `OrderService/Program.cs` with `PostgreSqlOutbox<Order>`, `PostgreSqlRepository<Order>`, `RabbitMqPublisher` targeting exchange `raytree.changes` (topic), JSON serializer
- [ ] 3.2 Set `RabbitMqPublisherOptions.RoutingKey = "change"` to produce keys like `change.Order.insert`
- [ ] 3.3 Read PostgreSQL connection string from `POSTGRES_CONNECTION` environment variable (default pointing to `localhost`)
- [ ] 3.4 Read RabbitMQ host from `RABBITMQ_HOST` environment variable (default `localhost`)
- [ ] 3.5 Implement a loop that periodically creates, updates, and deletes `Order` entities by calling the repository for persistence and `tracker.TrackInsertAsync` / `TrackUpdateAsync` / `TrackDeleteAsync` for change tracking
- [ ] 3.6 Add console logging to show which change was tracked and the current order state

## 4. NotificationService Implementation

- [ ] 4.1 Configure `ChangeTrackingBuilder` in `NotificationService/Program.cs` with `RabbitMqConsumer` bound to exchange `raytree.changes`, queue `notification-service`, routing key `change.Order.*`, JSON serializer
- [ ] 4.2 Register `OnInsert` handler that logs the new order details
- [ ] 4.3 Register `OnUpdate` handler that logs the updated order details
- [ ] 4.4 Register `OnDelete` handler that logs the deleted order's ID
- [ ] 4.5 Read RabbitMQ host from `RABBITMQ_HOST` environment variable (default `localhost`)
- [ ] 4.6 Call `tracker.StartAsync` and keep the service running until cancellation

## 5. Docker Compose

- [ ] 5.1 Create `docker-compose.yml` with `postgres:16` service on port 5432, with a `healthcheck` (`pg_isready`) so dependent services can wait for readiness
- [ ] 5.2 Add `rabbitmq:3-management` service on ports 5672 and 15672
- [ ] 5.3 Add `order-service` with `RABBITMQ_HOST=rabbitmq`, `POSTGRES_CONNECTION=...` pointing to the compose postgres service, and `depends_on: {postgres: {condition: service_healthy}, rabbitmq: {condition: service_started}}`
- [ ] 5.4 Add `notification-service` with `RABBITMQ_HOST=rabbitmq` and `depends_on: rabbitmq`
- [ ] 5.5 Add `Dockerfile` for `OrderService` (multi-stage: SDK build → runtime image)
- [ ] 5.6 Add `Dockerfile` for `NotificationService` (multi-stage: SDK build → runtime image)

## 6. Documentation

- [ ] 6.1 Write `README.md` covering prerequisites (Docker), `docker compose up` instructions, expected console output, RabbitMQ management UI URL (port 15672), PostgreSQL connection details, and project structure overview
- [ ] 6.2 Note in README that `PostgreSqlOutbox` schema is created automatically on startup and survives restarts
- [ ] 6.3 Add inline code comments in `Program.cs` files explaining the key builder calls
