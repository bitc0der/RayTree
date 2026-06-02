# Changelog

All notable changes to this project will be documented in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## [0.0.20-pre-release]

- Minor cleanup on `IOutbox`

---

## [0.0.19-pre-release]

### Removed — BREAKING
- `ConnectionRecoveryOptions` (`RayTree.Core.Resilience`) and `ChangeTrackingRecoveryKeys` (`RayTree.Hosting`) are removed, along with the `ChangeTracking:Publisher:ConnectionRecovery` / `:Subscriber:ConnectionRecovery` config binding in `AddChangeTracking`. Each recovery-owning plugin now defines its own options type with identical field names, defaults, and validation: `PostgresConnectionRecoveryOptions` (`RayTree.Plugins.PostgreSQL.Resilience`) and `KafkaConnectionRecoveryOptions` (`RayTree.Plugins.Kafka`). The `ConnectionRecovery` property on `NotificationBasedPublisherOptions`, `KafkaPublisherOptions`, and `KafkaConsumerOptions` now returns the plugin-local type.
- All four `raytree.connection.*` metric instruments (`disconnects`, `recoveries`, `recovery.duration`, `state`) are removed, along with the `RayTreeMeter` facade methods `RecordConnectionDisconnect`, `RecordConnectionRecovery`, and `RegisterConnectionStateGauge`. **Disconnect/recovery visibility is now log-only** (Postgres/Kafka retry + recovery + exhaustion logs; RabbitMQ publisher `Warning`/`Information`). The RabbitMQ consumer no longer observes recovery at all (no logger, no metrics). Dashboards/alerts built on any `raytree.connection.*` series break silently — a removed series produces no error, just a flat-lined chart.

### Changed — BREAKING
- `RayTreeMeter` exposes **no public metric-emission API**. `RecordPublishSuccess`, `RecordPublishFailure`, `RecordPayloadSize`, `RecordBatchSize`, and `RegisterPendingGauge` are now `internal` (all callers — Core and the IVT-privileged `NotificationBasedPublisher` — already see Core internals). The public surface collapses to `MeterName`, the constructors, `DefaultPendingCacheTtl`, and `Dispose()` — construct-and-observe only. Metric observation remains public via the `"RayTree"` meter name and `RayTree.OpenTelemetry`'s `AddRayTreeMetrics`.
- `RabbitMqPublisher` and `RabbitMqConsumer` constructors no longer take a `RayTreeMeter?` parameter; `KafkaPublisher` and `KafkaConsumer` likewise drop their `RayTreeMeter?` parameter. The RabbitMQ/Kafka builder + subscriber extensions no longer forward a meter. Most callers go through the builder extensions and are unaffected; direct constructor callers drop the argument.

### Migration
- Recovery options — rename the type at the use site; field names and JSON keys below the section parent are unchanged:

  ```csharp
  // Before
  builder.UseKafka(o => o.ConnectionRecovery = new RayTree.Core.Resilience.ConnectionRecoveryOptions { MaxAttempts = 5 });
  // After
  builder.UseKafka(o => o.ConnectionRecovery = new KafkaConnectionRecoveryOptions { MaxAttempts = 5 });
  ```

  The former host-bound `ChangeTracking:*:ConnectionRecovery` sections never auto-applied (callers had to merge them by hand); set recovery in the `UseKafka` / `UsePostgreSqlOutbox` configure lambda instead, reading your own bound section if desired.
- Observability — drop any `raytree.connection.*` series from dashboards/alerts and switch to the recovery log signal.

---

## [0.0.18-pre-release]

### Added
- Connection-loss recovery and observability across every connection-bearing plugin. `NotificationBasedPublisher` now reopens the LISTEN connection with exponential backoff; `KafkaPublisher` rebuilds its native producer on fatal error via the lazy `GetProducerAsync` path; `KafkaConsumer` rebuilds on the dedicated poll thread; RabbitMQ plugins delegate to `AutomaticRecoveryEnabled` and emit metrics from SDK events; `PostgreSqlOutbox` + `OutboxPublisherService` observe outbox connection faults (no rebuild — polling cadence is the retry).
- Four shared metric instruments on the `"RayTree"` meter: `raytree.connection.disconnects`, `raytree.connection.recoveries` (with `outcome` tag), `raytree.connection.recovery.duration`, `raytree.connection.state` (observable gauge). All tagged with `component` + `endpoint`.
- `ConnectionRecoveryOptions` (`RayTree.Core.Resilience`) — tunes per-plugin exponential backoff (`Enabled`, `InitialDelay`, `MaxDelay`, `Factor`, `JitterFraction`, `MaxAttempts`). Bound from `ChangeTracking:Publisher:ConnectionRecovery` / `:Subscriber:ConnectionRecovery` via `AddChangeTracking` into NAMED options; keys exposed as `ChangeTrackingRecoveryKeys.Publisher` / `.Subscriber`.
- `IOutbox` gains default-implemented `IsConnectionFault(Exception)`, `ConnectionComponent`, `ConnectionEndpoint`. `PostgreSqlOutbox<TEntity>` overrides all three; other implementations inherit no-ops.
- Public facade methods on `RayTreeMeter`: `RecordConnectionDisconnect`, `RecordConnectionRecovery`, `RegisterConnectionStateGauge`. No new `InternalsVisibleTo` entries.

### Changed
- `OutboxPublisherService` is now a thin wrapper over a generic `TypedImpl<TEntity>`. Per-batch path is zero-reflection; public surface unchanged.

### Changed — BINARY-BREAKING
- `KafkaPublisher`, `KafkaConsumer`, `RabbitMqPublisher`, `RabbitMqConsumer` constructors add optional `RayTreeMeter? meter = null`. Source-compatible; downstream NuGet consumers must recompile. Builder extensions forward the parameter.

---

## [0.0.17-pre-release]

### Added
- Optional `WaitForTopic` retry for Kafka publisher and consumer (mirrors RabbitMQ `WaitForTopology`). New options: `WaitForTopic` (default `false`), `TopicWaitInterval` (5 s), `TopicWaitTimeout` (`null`). Probes via `IAdminClient.GetMetadata`; retries on transient broker/transport conditions; propagates fatal/authorization errors. Both `UseKafka` builder extensions now accept an optional `ILoggerFactory?`.

### Changed — BINARY-BREAKING
- `KafkaPublisher` constructor adds optional `ILoggerFactory? loggerFactory = null`.

### Changed
- `KafkaPublisher` splits one-shot probe semaphore from the producer-build semaphore so concurrent publishers don't serialize behind a multi-second probe.
- `KafkaPublisher.Dispose` is idempotent; `SafeRelease` swallows `ObjectDisposedException` during Dispose-during-init races.
- `KafkaConsumer.InitializeAsync` is genuinely async; cancellation re-check between probe and native handle allocation prevents handle leaks.
- `ChangeSubscriber.InitializeAsync` initializes all consumers in parallel via `Task.WhenAll`.

---

## [0.0.16-pre-release]

### Added
- Structured configuration- and lifecycle-time logging through the entire builder + tracker path. `Information` for global `Use*` and `ForEntity<TEntity>`; `Debug` for per-entity overrides and handler registration (with `{Plugin}` walking past compiler-generated closure types); `Information` "ChangeTracker built" summary; `Information`/`Debug`/`Warning` tracker initialization markers; `Information` `ChangeTrackingHostedService` startup log with `{ConfigurationBound}`. Each builder owns `ILogger<Self>` for per-category filtering. All calls guarded by `IsEnabled` — zero overhead under `NullLoggerFactory`.
- `Build()` / `BuildAsync()` now dispose the tracker if `InitializeAsync` throws (previously leaked owned `RayTreeMeter`, publisher services, dedup store).
- `AddChangeTracking` registration is idempotent via `TryAddSingleton`.
- Kafka microservices example (`examples/Kafka.Microservices`) — full outbox-to-Kafka pipeline with OrderService + NotificationService, Dockerized, demonstrates partition-key ordering, consumer-group scaling, `FromEarliest` replay, isolated-handler dispatch.
- `examples/RabbitMQ.Microservices` README expanded; `docs/README.md` gains an Examples section.

---

## [0.0.15-pre-release]

### Added
- Topology wait for externally-owned RabbitMQ topology. New options on both `RabbitMqPublisherOptions` and `RabbitMqConsumerOptions`: `WaitForTopology` (default `false`), `TopologyWaitInterval` (5 s), `TopologyWaitTimeout` (`null`). Publisher probes the exchange when `DeclareExchange = false`; consumer probes the queue when `DeclareQueue = false` and the binding-target exchange when `ExchangeName` is non-empty. Only `NOT_FOUND` (404) triggers retry — `PRECONDITION_FAILED`, `ACCESS_REFUSED`, connection failures propagate immediately. Fresh channel per attempt.

---

## [0.0.14-pre-release]

### Added
- `RabbitMqPublisherOptions.RoutingKeySelector` — configurable AMQP routing key (default preserves `"{RoutingKey}.{EntityType}.{changeType}"`).
- `KafkaPublisherOptions.KeySelector` — configurable Kafka partition key (default preserves `"{EntityType}:{EntityId}"`).

---

## [0.0.13-pre-release]

### Added
- `EntityChangeTracker.StartAsync(CancellationToken)` / `StopAsync()` — explicit consumer lifecycle directly on the tracker. `ChangeTrackingHostedService` calls these automatically.
- `EntityChangeTracker.RunCleanupAsync(retentionPeriod, ct)` — iterates every registered outbox and returns the total deleted-row count.

### Changed (breaking)
- `EntityChangeTracker.Publisher` and `Subscriber` are now `internal`. Plugin assemblies access via `InternalsVisibleTo`.
- `EntityChangeTracker.InitializeAsync()` is now `internal` — `Build()` / `BuildAsync()` invoke it automatically.
- `NotificationBasedPublisher` first ctor arg changes from `ChangePublisher` to `EntityChangeTracker`.

### Removed
- `OutboxCleanupService` — call `tracker.RunCleanupAsync` directly. `AddChangeTracking` no longer registers it.

### Fixed
- `EntityChangeTracker.InitializeAsync` now initializes isolated-mode consumer queues (previously only shared-mode queues were initialized; isolated consumers started unconnected).

---

## [0.0.12-pre-release]

### Added
- `EntityChangeTracker.Create(ILoggerFactory? = null)` — canonical entry point returning `IChangeTrackingBuilder`.

### Changed (breaking)
- `ChangeTrackingBuilder` constructor is `internal`; class is `sealed`. Use `EntityChangeTracker.Create()`.
- `EntityChangeTracker` constructor is `internal`. Use `EntityChangeTracker.Create()` or `AddChangeTracking`.

### Fixed
- Duplicate `<see cref>` XML doc tag in `ChangeSubscriberBuilder.Build()`.

---

## [0.0.11-pre-release]

### Added
- `RayTree.Plugins.Deduplication.Redis` — `RedisDeduplicationStore` backed by Redis `SET NX EX` with TTL-based expiry (`CleanupAsync` is a no-op). `RedisDeduplicationOptions` configures `KeyPrefix`, `RetentionPeriod`, logical DB index. `UseRedisDeduplication(IConnectionMultiplexer, …)` extensions on both `IChangeSubscriberBuilder` and `IChangeTrackingBuilder`. Key format: `raytree:dedup:{KeyPrefix}:{correlationId}`. `InMemoryDeduplicationStore` remains the default.

---

## [0.0.10-pre-release]

### Changed (breaking)
- `ChangeType` is required on every handler registration — `OnChange(changeType, handler)` takes a non-nullable `ChangeType`; the wildcard `null` form is removed. Dispatch is a strict equality check. `HandlerRegistration.ChangeType` is non-nullable. Migration: register one handler per change type. (`IOutbox.GetUnpublishedAsync(ChangeType? …)` query filter is unaffected.)
- `IEntityBuilder<TEntity>` — `OnInsert`/`OnUpdate`/`OnDelete`/`OnChange` removed; handler registration is only reachable via the post-fork builders. `UseConsumer` return type changes to `ISharedHandlerBuilder<TEntity>`. Migration: reorder so `UseSerializer`/`UseCompressor`/`UseSubscriberOptions` precede `UseConsumer`.

### Added
- Optional at-least-once delivery. `IQueueConsumer` gains default-no-op `AcknowledgeAsync` / `NegativeAcknowledgeAsync`. RabbitMQ and Kafka opt in via `AckAfterHandler = true`; ACK/commit deferred until handler success, NACK requeues (Rabbit `BasicNack`) or seeks back (Kafka `Seek` on poll thread). Broker-private state travels via `MessageEnvelope.Metadata` (lazy-allocated). Kafka requires `MaxDegreeOfParallelism = 1` per partition when enabled.
- Handler dispatch modes. **Shared**: `UseConsumer(IQueueConsumer)` → `ISharedHandlerBuilder<TEntity>`, single delivery, sequential handlers, dedup key `correlationId`. **Isolated**: `UseConsumerFactory(Func<string, IQueueConsumer>)` → `IIsolatedHandlerBuilder<TEntity>`, per-named-handler subscription / retry budget / dedup namespace (`$"{correlationId}:{handlerName}"`); per-handler `SubscriberOptions?` parameter on named registrations.
- `EntityHandlerKey` record struct — typed dictionary key for `ChangeSubscriber.IsolatedQueues`.
- `InMemoryBroadcastQueue` — fan-out queue for isolated-mode testing.

### Known limitation
- Shared-mode broker ACK fires before subscriber processing on Rabbit/Kafka, so dedup-revert retry is best-effort on those brokers (strong on `InMemoryQueue`). Isolated mode unaffected. Fixed in 0.0.10 follow-up via `AckAfterHandler`.

---

## [0.0.9-pre-release]

### Added
- OpenTelemetry metrics. `RayTreeMeter` owns a `System.Diagnostics.Metrics.Meter("RayTree", <version>)` and the full set of 14 instruments across outbox + subscriber paths, plus a `raytree.outbox.pending` observable gauge (cached for `DefaultPendingCacheTtl = 10 s`, configurable via ctor overload). All instruments tagged with `entity_type`; change-specific add `change_type`; skipped-messages adds `reason`. `RayTreeMeter.MeterName` is a public constant.
- `UseMeter(RayTreeMeter)` on `IChangeTrackingBuilder`. Tracker tracks ownership via `ownsMeter`: auto-created meters are disposed; caller-supplied are left alone. `EntityChangeTracker.Meter` exposes the instance. `AddChangeTracking` registers `RayTreeMeter` as a DI singleton and feeds it via `UseMeter`.
- `RayTree.OpenTelemetry` peer assembly — `RayTreeInstrumentation.MeterName` + `AddRayTreeMetrics` extension. `RayTree.Core` and `RayTree.Hosting` retain zero OTel dependencies.
- Docs: `docs/opentelemetry-metrics.md` with full inventory, wire-up examples, bucket boundaries, Prometheus queries.

### Breaking Changes
- `IOutbox.GetPendingCountAsync(Type entityType, CancellationToken)` added — used by the pending gauge. External implementations must add it.
- `ChangePublisher`, `OutboxPublisherService`, `ChangeSubscriber` constructors require non-nullable `RayTreeMeter`. No internal fallback — the builder layer constructs a default.
- `RabbitMqConsumer` constructor no longer accepts `ILoggerFactory`. Builder call-sites unaffected.

### Removed
- `RayTree.Plugins.Serializers.MsgPack` — duplicate of `RayTree.Plugins.Serializers.MessagePack`. Use the latter.

### Fixed
- `GetPendingCountAsync_OnEmptyTable_ReturnsZero` — added `[TearDown]` to truncate the shared table between tests.

### Infrastructure
- Solution format migrated `.sln` → `.slnx`. Update local scripts/IDE settings.
- `SECURITY.md` added.

### Dependencies
- Bumped `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.EntityFrameworkCore`, `Microsoft.EntityFrameworkCore.InMemory` 10.0.7 → 10.0.8.
- Added `Microsoft.Extensions.Hosting.Abstractions` (replaces full `Hosting`), `Microsoft.Extensions.Options.ConfigurationExtensions` (replaces `Configuration.Binder`), `OpenTelemetry` + `OpenTelemetry.Api` 1.15.3 (in `RayTree.OpenTelemetry` only).
- Removed unused direct refs: `Microsoft.EntityFrameworkCore.Relational`, `Microsoft.Extensions.DependencyInjection`.

---

## [0.0.8-pre-release]

### Breaking Changes
- `UseQueue` split into `UsePublisher` (publisher contexts) and `UseConsumer` (subscriber contexts) across `IChangeTrackingBuilder`, `IChangePublisherBuilder`, `IEntityBuilder<TEntity>`, `IEntityPublisherBuilder<TEntity>`, `IEntitySubscriberBuilder<TEntity>`. All plugin extensions updated. Behaviour unchanged.

---

## [0.0.7-pre-release]

### Added
- Automatic PostgreSQL schema migration in `PostgreSqlOutbox<TEntity>` and `PostgreSqlRepository<TEntity>` — no `AutoMigrate` flag, always on. Fresh table: single `CREATE TABLE IF NOT EXISTS`. Existing table: column diff (`SchemaMigrator` — adds missing columns with `ALTER TABLE … ADD COLUMN IF NOT EXISTS`; fail-fast on NOT-NULL without default on non-empty table; `Warning` for orphan columns / type mismatches) + index diff (`IndexMigrator` — creates missing, DROP+CREATE for changed definitions, `Warning` for orphans).
- New internals: `SchemaInspector`, `SchemaMigrator`, `IndexMigrator`, `PostgreSqlTypeNormalizer`.

### Breaking Changes
- `PostgreSqlOutbox<TEntity>` and `PostgreSqlRepository<TEntity>` constructors require `ILoggerFactory` as the second parameter. Builder extensions absorb the default (`NullLoggerFactory.Instance`); direct construction must add the arg.

---

## [0.0.6-pre-release]

### Fixed
- Duplicate publish prevention in `NotificationBasedPublisher`: `OutboxPublisherService` now respects `UseNotificationChannel` (uses `FallbackPollingInterval` when true); fallback polling only runs on first tick or when LISTEN is unhealthy; `OnNotification` and `ProcessUnpublishedChangesAsync` atomically claim records via `IOutbox.TryClaimForPublishingAsync` (revert on failure).
- Deduplication correctness in `ChangeSubscriber`: dedup mark is reverted via `IDeduplicationStore.RevertProcessedAsync` when a handler exhausts retries (`SkipOnFailure = false`) so the redelivered copy is retried instead of silently dropped by persistent stores. `MaybeDedupCleanupAsync` now actually wires `CleanupAsync` (gated by new `SubscriberOptions.DeduplicationCleanupInterval`, default 1 h), so `InMemoryDeduplicationStore` no longer grows unbounded. `IDeduplicationStore.IsProcessedAsync` removed (was never called).
- `OutboxPublisherOptions.MaxPublishConcurrency`, `NotificationBasedPublisherOptions.MaxPublishConcurrency`, `SubscriberOptions.MaxDegreeOfParallelism` defaults changed `Environment.ProcessorCount` → `1` (sequential) to preserve per-partition ordering.

### Added
- `IOutbox.TryClaimForPublishingAsync(long id, …)` / `RevertClaimAsync(long id, …)`.
- `IDeduplicationStore.RevertProcessedAsync(string correlationId, …)`.
- `SubscriberOptions.DeduplicationCleanupInterval` (default 1 h).
- Throughput: `Parallel.ForEachAsync`-bounded publish/consume loops; skip inter-batch sleep when batch is full; `NotificationBasedPublisherOptions.MaxConcurrentNotifications` (default 16, `SemaphoreSlim`-bounded — overflow falls to polling).
- Logging: `NotificationBasedPublisher` LISTEN loss `Warning` only on first unhealthy tick + recovery `Information`; claim contention `Debug`. `ChangeSubscriber` successful dispatch `Debug`; dedup revert `Warning` on retry exhaustion. `ChangeTrackingHostedService` consumer loop start log includes entity type name.

### Breaking Changes
- `IOutbox` gains `TryClaimForPublishingAsync` + `RevertClaimAsync`. External implementations must add both.
- `IDeduplicationStore` loses `IsProcessedAsync`, gains `RevertProcessedAsync`.

---

## [0.0.5-pre-release]

### Added
- 1D primitive-array support for PostgreSQL outbox/repository. `EntityColumnMapper.ToPostgresType` maps `int[]`/`long[]`/`short[]`/`byte[]`/`sbyte[]`/`float[]`/`double[]`/`decimal[]`/`bool[]`/`Guid[]`/`DateTime[]`/`DateTimeOffset[]`/`string[]` to the corresponding PostgreSQL array type. Nullable element wrappers stripped. Multi-dim arrays unsupported (use `[Column(TypeName = "…")]`). New shared `EntityColumnMapper.ConvertFromDb(object, Type)` helper for the read path.

### Fixed
- `OutboxPublisherService.MaybeRunCleanupAsync` now only advances `_lastCleanup` on success — a transient DB failure no longer delays the next retry by a full `CleanupInterval`.
- `PostgreSqlOutbox.BatchDeleteAsync` reuses a single `NpgsqlCommand` across batch iterations.

---

## [0.0.4-pre-release]

### Added
- Outbox rotation integrated into `OutboxPublisherService` — runs `MaybeRunCleanupAsync` after every poll batch (eager first tick, then gated by `CleanupInterval`). Errors isolated; do not abort the publish loop.
- `IOutbox.CleanupStaleUnpublishedAsync(TimeSpan, …)` — deletes unpublished records older than the threshold and logs `Warning` on any hit (operator signal). Opt-in via `OutboxPublisherOptions.StaleUnpublishedThreshold` (default `null`).
- `OutboxPublisherOptions`: `CleanupRetentionPeriod` (7 days), `CleanupInterval` (1 h), `StaleUnpublishedThreshold` (null).
- PostgreSQL: batched cleanup via `DELETE … WHERE id IN (SELECT id … LIMIT @BatchSize)`; `PostgreSqlOutboxOptions.CleanupBatchSize` (default 1000); new partial index `idx_*_outbox_cleanup` on `(timestamp) WHERE published = TRUE`.

### Breaking Changes
- `IOutbox.CleanupStaleUnpublishedAsync` added — external implementations must add it.

### Fixed
- `ServiceCollectionExtensions` was passing `PollingInterval * 10` (~50 s) as retention; now uses `CleanupRetentionPeriod` (7 days).
- PostgreSQL integration tests no longer race with the background publisher poll loop (outbox created directly in `SetUp`).

### Dependencies
- Bumped EF Core, EF Core InMemory + Relational, DI, Hosting, Configuration.Binder, Npgsql 8.x → 10.0.x.
- Removed unused `Microsoft.Extensions.Options`, `System.IO.Pipelines`, `StackExchange.Redis`.

---

## [0.0.3-pre-release]

### Added
- `[Key]` primary key support. `EntityColumnMapper.GetKeyProperties(Type)` resolves single + composite keys (composite ordered by `[Column(Order)]` then declaration); falls back to `Id` convention; throws at construction (fail-fast) when neither exists. Used by `PostgreSqlRepository<TEntity>` for INSERT/WHERE and source-table UNIQUE index, and by `InMemoryRepository<TEntity>` (composite keys serialised with `\0` separator).
- DataAnnotations attribute support for PostgreSQL schema: `[NotMapped]`, `[Column("name")]`, `[Column(TypeName = "…")]`, `[Required]`, `[MaxLength(n)]` / `[StringLength(n)]`, `[Table("name")]`. New `EntityColumnMapper.GetTableName(Type)` used by both outbox + repository defaults.

### Changed
- `IRepository<TEntity>.GetByIdAsync(object id, …)` → `GetByIdAsync(object[] keyValues, …)`. Both implementations validate the count.
- `PostgreSqlRepository<TEntity>` builds a `columnName → PropertyInfo` cache at construction; `MapEntity` is pure dictionary lookup.
- Target framework `net8.0` → `net10.0`.

### Fixed
- `InMemoryRepository` composite-key separator `|` → `\0` (null char) to prevent collisions when values contain `|`.

---

## [0.0.2] — 2026-05-09

### Added
- Structured logging via `Microsoft.Extensions.Logging` throughout. `ILoggerFactory` parameter on `ChangeTrackingBuilder`, `KafkaConsumer`, `RabbitMqConsumer`, `ChangeTrackingHostedService`. Defaults to `NullLoggerFactory`.

---

## [0.0.1] — initial release

- Initial implementation: entity change tracking with outbox pattern, in-memory + PostgreSQL plugins, Kafka + RabbitMQ queue plugins, JSON / MessagePack / Protobuf serialisers, Gzip / Brotli / LZ4 compressors, EF Core interceptor, ASP.NET Core hosting integration.
