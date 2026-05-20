## Context

RayTree has full RabbitMQ support (`RabbitMqPublisher`, `RabbitMqConsumer`, routing key selectors, `AckAfterHandler`) but no runnable multi-service example. All existing RabbitMQ coverage lives in integration tests (`RayTree.Plugins.RabbitMQ.Tests`) that use Testcontainers and focus on correctness, not on illustrating real-world wiring. Developers starting a new microservice project have no reference for how to configure the builder, wire the exchange/queue topology, or structure handler code.

The example must be completely self-contained: it cannot modify library source, must not be compiled as part of the main solution, and must run with a single `docker compose up`.

## Goals / Non-Goals

**Goals:**
- Show a realistic producer microservice (`OrderService`) that tracks `Order` entity changes into an outbox and publishes to RabbitMQ.
- Show a realistic consumer microservice (`NotificationService`) that subscribes to order changes with wildcard routing key bindings and dispatches to typed handlers.
- Demonstrate shared-consumer handler dispatch mode (all handlers on one consumer, registration order preserved).
- Include `docker-compose.yml` that starts PostgreSQL, RabbitMQ (with management UI), and both services.
- Use `PostgreSqlOutbox<Order>` and `PostgreSqlRepository<Order>` in `OrderService` to demonstrate durable outbox and persistent entity storage.
- Produce `README.md` explaining how to run and what to observe.

**Non-Goals:**
- Isolated-consumer (per-handler subscription) mode — shared mode is sufficient to illustrate the pattern.
- EF Core integration — that complexity belongs in a separate example.
- OpenTelemetry wiring — out of scope for this example.
- Production-hardened configuration (connection retry policies, TLS) — kept simple for readability.
- Changing any library source file or existing test.

## Decisions

### D1: Separate .NET solution file for the example

The example uses its own `RabbitMQ.Microservices.slnx` rather than being added to `RayTree.slnx`. This keeps CI unaffected and makes it obvious the example is standalone. The example references RayTree projects via `<ProjectReference>` so developers can hack on both simultaneously without a NuGet feed.

**Alternative considered**: add to main solution as a non-test project. Rejected — it would add build time to the CI matrix and couple the example's compile state to every library PR.

### D2: Two console apps in one solution, not two separate repos

`OrderService` and `NotificationService` are both console apps in `examples/RabbitMQ.Microservices/`. Keeping them together simplifies the `docker-compose.yml` and the README instructions. Each is a `Program.cs` file using top-level statements.

**Alternative considered**: separate repos / separate solutions. Rejected — too much friction for a learning example.

### D3: PostgreSQL outbox and repository

`PostgreSqlOutbox<Order>` and `PostgreSqlRepository<Order>` (`RayTree.Plugins.PostgreSQL`) are used for `OrderService`. This demonstrates the durable, production-realistic path: the outbox survives restarts, schema migration runs automatically on `InitializeAsync`, and the repository provides typed INSERT/UPDATE/DELETE/SELECT operations keyed by `[Key]` on `Order.Id`. Both connect via the same `POSTGRES_CONNECTION` environment variable.

`NotificationService` is publisher-free (subscriber only) and needs no outbox or repository.

**Alternative considered**: keep the in-memory outbox to avoid adding PostgreSQL to the compose stack. Rejected — the user explicitly asked for PostgreSQL storage and outbox; in-memory would not demonstrate the durable pattern.

**Alternative considered**: separate connection strings for outbox and repository. Rejected — they target the same database in the example; a single env var keeps configuration minimal.

### D4: Shared-handler dispatch mode

The `NotificationService` uses `UseConsumer(consumer)` (shared mode) with `OnInsert`, `OnUpdate`, and `OnDelete` handlers on the same `ISharedHandlerBuilder<Order>` chain. This matches the common case and maps directly to the fluent API shown in unit tests.

### D5: Routing key pattern

`OrderService` uses the default `RabbitMqPublisherOptions.RoutingKey = "change"`, producing keys like `change.Order.insert`. `NotificationService` binds with `change.Order.*` to receive all change types. This demonstrates the wildcard routing pattern without requiring a custom `RoutingKeySelector`.

### D6: Exchange topology

A single `topic` exchange named `raytree.changes` is declared by both services (declare-or-verify, idempotent). `OrderService` declares it on startup; `NotificationService` also declares it and binds its queue. RabbitMQ's passive/active declare semantics make the order of startup irrelevant.

## Risks / Trade-offs

- **PostgreSQL schema migration runs on every startup** → `InitializeAsync` is idempotent (`CREATE TABLE IF NOT EXISTS`, column diff, index diff) so this is safe; adds a few hundred milliseconds to startup. Acceptable for an example.
- **Both services reference library projects directly** → If the library has a compile error the example also fails. Benefit: no NuGet publish step needed for local development.
- **Hard-coded localhost connection strings** → Works for `docker compose up`; real deployments need env-var overrides. README notes this and shows the `RABBITMQ_HOST` / `POSTGRES_CONNECTION` environment variable pattern.
- **No retry / reconnect logic** → RabbitMQ or PostgreSQL connection failures will crash the example services. Acceptable for demonstration purposes; production guidance belongs in docs, not this example.
- **Docker Compose startup ordering** → `OrderService` must not attempt a DB connection before PostgreSQL is ready. Mitigated by a `healthcheck` on the `postgres` service and `condition: service_healthy` in `order-service`'s `depends_on`.
