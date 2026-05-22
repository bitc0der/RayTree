## Why

The library has a runnable RabbitMQ multi-service example but no equivalent for Kafka. Developers evaluating RayTree in a Kafka-based platform need a concrete, working reference that shows how to wire producer and consumer microservices end-to-end — including PostgreSQL-backed outbox, partition-key routing, and Docker Compose spin-up — without relying on the RabbitMQ example for guidance.

## What Changes

- Add a new `examples/Kafka.Microservices/` solution demonstrating two microservices: an `OrderService` that tracks order entity changes via a PostgreSQL outbox and publishes to Kafka, and a `NotificationService` that consumes those changes and handles them.
- The example covers: PostgreSQL-backed outbox and repository for `OrderService`; `KafkaPublisher` targeting a single topic with the default partition key (`EntityType:EntityId`); `KafkaConsumer` in `NotificationService`; MessagePack serialization with Gzip compression; shared-handler dispatch mode; and Docker Compose for local spin-up (PostgreSQL + Kafka + Zookeeper + both services).
- No changes to library source code or existing tests.

## Capabilities

### New Capabilities

- `kafka-microservices-example`: A self-contained multi-microservice example project demonstrating RayTree's Kafka integration for entity change streaming across service boundaries.

### Modified Capabilities

<!-- none -->

## Impact

- New `examples/Kafka.Microservices/` directory; no changes to existing library source, tests, or CI.
- Requires `RayTree.Core`, `RayTree.Hosting`, `RayTree.Plugins.PostgreSQL`, `RayTree.Plugins.Kafka`, `RayTree.Plugins.Serializers.MessagePack`, and `RayTree.Plugins.Compressors.Gzip` project references (all exist in the solution).
- Example is excluded from the main `RayTree.slnx` solution file but can be opened standalone.
- Adds a `docker-compose.yml` with Kafka (KRaft mode — no Zookeeper), PostgreSQL, and both services; does not affect existing Testcontainers-based integration tests.
