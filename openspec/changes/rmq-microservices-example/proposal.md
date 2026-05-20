## Why

The library lacks a concrete, runnable reference showing how multiple microservices interact via RabbitMQ using RayTree for entity change streaming. Developers evaluating or onboarding to RayTree need a realistic multi-service scenario — not just unit tests — to understand producer/consumer wiring, routing key patterns, and subscriber handler setup end-to-end.

## What Changes

- Add a new `examples/RabbitMQ.Microservices/` solution demonstrating two microservices (an `OrderService` that tracks order entity changes and a `NotificationService` that consumes them) connected via RabbitMQ.
- The example covers: PostgreSQL-backed outbox and repository for `OrderService`, RabbitMQ publisher/consumer configuration, MessagePack serialization with Gzip compression on the payload pipeline, routing key patterns (`change.Order.*`), shared-handler dispatch mode, and Docker Compose for local spin-up (PostgreSQL + RabbitMQ + both services).
- No changes to library source code or existing tests.

## Capabilities

### New Capabilities

- `rmq-microservices-example`: A self-contained multi-microservice example project demonstrating RayTree's RabbitMQ integration for entity change streaming across service boundaries.

### Modified Capabilities

<!-- none -->

## Impact

- New top-level `examples/` directory; no changes to existing library source, tests, or CI.
- Requires `RayTree.Core`, `RayTree.Hosting`, `RayTree.Plugins.PostgreSQL`, `RayTree.Plugins.RabbitMQ`, `RayTree.Plugins.Serializers.MessagePack`, and `RayTree.Plugins.Compressors.Gzip` project references (all exist in the solution).
- Example is excluded from the main `RayTree.slnx` solution file but can be opened standalone.
- Adds a `docker-compose.yml` with RabbitMQ and PostgreSQL services; does not affect existing Testcontainers-based integration tests.
