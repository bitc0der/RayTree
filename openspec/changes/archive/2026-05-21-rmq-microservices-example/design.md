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

`OrderService` relies on the default `RabbitMqPublisherOptions.RoutingKey` value (`"change"`) — no override needed — which produces keys like `change.Order.insert`. `NotificationService` binds with `change.Order.*` to receive all change types. This demonstrates the wildcard routing pattern without requiring a custom `RoutingKeySelector`.

### D6: Exchange topology

A single `topic` exchange named `raytree.changes` is declared by both services (declare-or-verify, idempotent). `OrderService` declares it on startup; `NotificationService` also declares it and binds its queue. RabbitMQ's passive/active declare semantics make the order of startup irrelevant. The `NotificationService` queue is named `notification-service.orders`, declared `durable: true, exclusive: false, autoDelete: false` so it survives restarts and can be horizontally scaled (multiple consumer replicas competing for the same queue).

### D7: `RayTree.Hosting` over raw builder

Both services use `Host.CreateApplicationBuilder(args)` + `services.AddChangeTracking(configuration, configure)` from `RayTree.Hosting`, not the raw `ChangeTrackingBuilder`. This is the documented "primary registration path" in CLAUDE.md — it gives the example graceful shutdown via `IHostApplicationLifetime`, structured logging out of the box, and `IConfiguration` binding for `ChangeTracking:Publisher` / `ChangeTracking:Subscriber` sections without bespoke env-var parsing. The `ChangeTrackingHostedService` handles `StartAsync`/`StopAsync` automatically.

**Alternative considered**: raw `ChangeTrackingBuilder` + manual `Console.CancelKeyPress` wiring. Rejected — the example should showcase the recommended path, not the lower-level one.

### D8: Repository / outbox atomicity is a known simplification

`PostgreSqlRepository<Order>` and `PostgreSqlOutbox<Order>` each manage their own `NpgsqlConnection`. The library does not (yet) expose a shared-transaction API across them, so calling `repository.InsertAsync(order)` and then `tracker.TrackInsertAsync(order)` is two separate transactions. A crash between them can leave the orders table and outbox out of sync — the very problem the outbox pattern usually solves.

For this example, we accept the limitation and document it prominently in the README: "**This example is not transactionally safe between the entity table and the outbox.** Real production systems should use `RayTree.EntityFrameworkCore` (where the `EntityChangeInterceptor` writes both inside `SaveChangesAsync`'s transaction) or wrap both writes in a custom shared-connection transaction."

**Alternative considered**: include EF Core in this example to get true atomicity. Rejected — the EF wiring is a substantive additional surface and deserves a dedicated example; this one is about RabbitMQ-driven microservices. Adding EF would dilute the focus.

**Alternative considered**: order the calls so `TrackInsertAsync` runs first (outbox-only) and persistence happens only on subsequent retry. Rejected — that inverts the standard outbox pattern and would teach a confusing model.

### D9: Shared class library for the `Order` entity

A `Shared/Shared.csproj` class library project holds `Order.cs` and is referenced by both `OrderService` and `NotificationService`. This is cleaner than `<Compile Include="..\Shared\Order.cs" />` linked files: IDE refactors propagate correctly, the type identity is unambiguous, and the solution view groups related files naturally.

### D10: Local Directory.Build.props and Directory.Packages.props that inherit the root

The root `Directory.Build.props` carries packaging metadata (`<VersionPrefix>`, `<Authors>`, `<PackageLicenseExpression>`, etc.) intended for the *library* projects. The example console apps must not inherit that metadata blindly — they aren't packed, and pulling in `<IncludeSymbols>true</IncludeSymbols>` etc. would be wrong.

The root `Directory.Packages.props` also lacks `Microsoft.Extensions.Hosting` (only `Microsoft.Extensions.Hosting.Abstractions` is centrally pinned). The example needs the full Hosting package for `Host.CreateApplicationBuilder`. **The root `Directory.Packages.props` MUST NOT be modified** — the library's published API surface should not change just to support an example.

Solution: place local `examples/RabbitMQ.Microservices/Directory.Build.props` and `examples/RabbitMQ.Microservices/Directory.Packages.props` that each explicitly `<Import>` the parent file via `$([MSBuild]::GetPathOfFileAbove('<filename>', '$(MSBuildThisFileDirectory)../'))`, then add example-only overrides:

- The local `Directory.Build.props` imports the parent, then resets `<IsPackable>false</IsPackable>` and clears package metadata that does not apply to console apps.
- The local `Directory.Packages.props` imports the parent and appends `<PackageVersion Include="Microsoft.Extensions.Hosting" Version="10.0.8" />` (and any other example-only packages).

This keeps central package management active, the example self-contained, and the library's global config untouched.

**Alternative considered**: opt the example out of central package management by setting `<ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>` and hard-coding versions on every `PackageReference`. Rejected — it diverges from the codebase convention and creates two version-bookkeeping surfaces.

**Alternative considered**: add `Microsoft.Extensions.Hosting` to the root `Directory.Packages.props`. Rejected — the example must not pollute the library's central package manifest with packages no library project consumes.

### D11: MessagePack serializer + Gzip compressor

The payload pipeline uses `RayTree.Plugins.Serializers.MessagePack` (`.UseMessagePackSerializer()`) and `RayTree.Plugins.Compressors.Gzip` (`.UseGzipCompressor()`). MessagePack is a compact, schema-less binary format — payloads are roughly 30–50 % smaller than equivalent JSON before compression, with no codegen step or schema file required. The plugin uses `MessagePackSerializer.Typeless` with the contractless resolver, so the `Order` POCO needs no MessagePack-specific attributes (`[MessagePackObject]` / `[Key]`) — the existing `[Key] Id` from `System.ComponentModel.DataAnnotations` is unaffected because MessagePack's resolver does not consume that attribute.

Gzip compression sits on top, further shrinking payloads on the wire and in the outbox `payload` column. Both services must register the same serializer/compressor pair — RayTree does not negotiate format on a per-message basis. The `MessageEnvelope.Payload` byte array on the broker is therefore `gzip(messagepack(EntityChange<Order>))`.

**Alternative considered**: JSON + no compression. Rejected — JSON works but doesn't demonstrate the binary path, and an example is the natural place to showcase the more production-realistic choice. Gzip in particular illustrates the streaming compression contract (`IChangeCompressor`).

**Alternative considered**: Protobuf serializer. Rejected — Protobuf requires `.proto` files and codegen, adding scaffolding noise that distracts from the messaging story.

**Alternative considered**: Brotli or LZ4 compression. Rejected — Gzip is the most broadly recognised format and ships in every .NET runtime; the example aims for clarity over peak compression ratio.

## Risks / Trade-offs

- **PostgreSQL schema migration runs on every startup** → `InitializeAsync` is idempotent (`CREATE TABLE IF NOT EXISTS`, column diff, index diff) so this is safe; adds a few hundred milliseconds to startup. Acceptable for an example.
- **Both services reference library projects directly** → If the library has a compile error the example also fails. Benefit: no NuGet publish step needed for local development.
- **Hard-coded localhost connection strings** → Works for `docker compose up`; real deployments need env-var overrides. README notes this and shows the `RABBITMQ_HOST` / `POSTGRES_CONNECTION` environment variable pattern.
- **No retry / reconnect logic** → RabbitMQ or PostgreSQL connection failures will crash the example services. Acceptable for demonstration purposes; production guidance belongs in docs, not this example.
- **Docker Compose startup ordering** → `OrderService` must not attempt a DB connection before PostgreSQL is ready. Mitigated by a `healthcheck` on the `postgres` service and `condition: service_healthy` in `order-service`'s `depends_on`.
- **Non-atomic repository + outbox writes** → See D8. Documented explicitly in the README. The example demonstrates the *messaging* pattern; production transactional safety requires EF Core integration or custom transaction wrapping.
- **Default outbox `PollingInterval` of 5 s is sluggish for a demo** → Mitigated by setting `PollingInterval = TimeSpan.FromMilliseconds(500)` in `OrderService` so events appear within roughly half a second. README mentions NOTIFY/LISTEN (`UseNotificationChannel = true`) as the production fast-path.
