# Examples

Example projects demonstrating **RayTree** change-tracking pipelines with different message brokers.

| Example | Broker | Description |
|---|---|---|
| [Kafka.Microservices](Kafka.Microservices/README.md) | Kafka (KRaft) | OrderService publishes entity changes via a PostgreSQL outbox to a Kafka topic; NotificationService consumes and logs them. |
| [RabbitMQ.Microservices](RabbitMQ.Microservices/README.md) | RabbitMQ | Same two-service topology using RabbitMQ topic exchanges with routing keys per change type. |

Both examples share the same architecture: a simulator class inserts/updates/deletes `Order` rows, an outbox captures each change, and a background publisher loop serializes (MessagePack), compresses (Gzip), and forwards the change to the broker. A consumer service receives, decompresses, deserializes, and dispatches to per-type handlers.

## Prerequisites

- Docker with Compose v2 (24+)
- .NET SDK 10.0+ (only needed for local dev outside Docker)

## Quick start

```bash
# Kafka
cd examples/Kafka.Microservices
docker compose up --build

# RabbitMQ (in a separate terminal)
cd examples/RabbitMQ.Microservices
docker compose up --build
```

Each example directory has its own `README.md` with detailed walkthroughs, project structure, configuration options, and guidance on production-ready patterns (EF Core transactional outbox, NOTIFY/LISTEN fast-path, at-least-once delivery, isolated-handler dispatch, OpenTelemetry).
