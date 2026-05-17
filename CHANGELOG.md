# Changelog

All notable changes to this project will be documented in this file.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## [0.0.10-pre-release]

### Added

#### Optional at-least-once delivery (`RayTree.Core`, `RayTree.Plugins.RabbitMQ`, `RayTree.Plugins.Kafka`)

`IQueueConsumer` gains two default-no-op methods — `AcknowledgeAsync(MessageEnvelope, CancellationToken)`
and `NegativeAcknowledgeAsync(MessageEnvelope, CancellationToken)` — that `ChangeSubscriber`
invokes after each dispatched message: ACK on normal completion (handler success, dedup hit,
no-handler skip, `SkipOnFailure` swallow), NACK on retry-exhaustion with `SkipOnFailure = false`.
Existing custom `IQueueConsumer` implementations inherit the no-ops and behave unchanged
(source-compatible, binary-compatible). Both shared-mode (`ConsumeFromConsumerAsync`) and
isolated-mode (`ConsumeIsolatedFromConsumerAsync`) consume loops participate; the custom-reader
overload (`ConsumeFromQueueAsync<TQueue>`) is at-most-once by design.

**`MessageEnvelope.Metadata`** — lazy-allocated `IDictionary<string, object?>` for consumer-private
broker state (delivery tags, lock tokens, receipt handles). Not part of the wire format; not
inspected by handlers.

**RabbitMQ opt-in** (`RabbitMqConsumerOptions.AckAfterHandler`, default `false`): when `true`,
the broker ACK is deferred until handler completion. NACK requeues via `BasicNackAsync(requeue: true)`.
Delivery tag is stashed in `MessageEnvelope.Metadata` via the internal `RabbitMqEnvelopeMetadata`
take-on-read accessor so a double-Ack attempt is a silent no-op rather than a broker error.

**Kafka opt-in** (`KafkaConsumerOptions.AckAfterHandler`, default `false`): when `true`, the
offset commit is deferred. The subscriber posts the original `ConsumeResult` plus a
`Commit`/`SeekBack` discriminator to an internal post-handler channel; the poll thread drains
it at the top of each iteration (Confluent.Kafka requires `Consume`/`Commit`/`Seek` on the
same thread). When pending work is queued, the next `Consume()` uses `TimeSpan.Zero` so commits
don't wait a full poll cycle. NACK performs `_consumer.Seek(TopicPartitionOffset)` so the
failed message is redelivered in the same consumer process, not just on restart. Parse-failure
path always commits immediately to avoid poison-pilling the partition. Requires
`SubscriberOptions.MaxDegreeOfParallelism = 1` per partition.

```csharp
// At-most-once (default — unchanged):
new RabbitMqConsumer(new RabbitMqConsumerOptions { QueueName = "orders" });

// At-least-once (opt-in):
new RabbitMqConsumer(new RabbitMqConsumerOptions
{
    QueueName       = "orders",
    AckAfterHandler = true,
});
```

#### Handler dispatch modes — Shared and Isolated (`RayTree.Core`, `RayTree.Hosting`, `RayTree.Plugins.InMemory`)

Two explicit handler-dispatch strategies are now available, selected at consumer-binding time.

**`Shared` mode** (existing behaviour, now explicit and accumulating):

Call `IEntityBuilder<TEntity>.UseConsumer(IQueueConsumer)` to fork into
`ISharedHandlerBuilder<TEntity>`. Multiple calls to `OnInsert`, `OnUpdate`, `OnDelete`, or
`OnChange` on the returned builder *accumulate* handlers; they execute sequentially in
registration order on a single delivery of each message. Dedup key: `correlationId`.

**`Isolated` mode** (new):

Call `IEntityBuilder<TEntity>.UseConsumerFactory(Func<string, IQueueConsumer>)` to fork into
`IIsolatedHandlerBuilder<TEntity>`. Each named handler receives its own broker subscription
(factory invoked once per unique name at `Build()` time), retry budget, and dedup namespace
(key: `$"{correlationId}:{handlerName}"`). `ChangeTrackingHostedService` starts one consume
loop per `(entity type, handler name)` pair automatically.

**Per-handler `SubscriberOptions`** (new, `IIsolatedHandlerBuilder<TEntity>`):

Each named handler registration accepts an optional `SubscriberOptions? options = null`
parameter on `OnInsert`, `OnUpdate`, `OnDelete`, and `OnChange`. The first non-null options
supplied for a given handler name apply to that handler's consume loop (DOP, retry budget,
skip-on-failure). Options supplied on later registrations under the same name are ignored.
Per-handler options take precedence over entity-level and global options.

```csharp
.UseConsumerFactory(name => broadcast.Subscribe())
.OnInsert("read-model", handler, new SubscriberOptions { MaxRetries = 5 })
.OnInsert("notifier",   handler)   // inherits global/entity options
```

**`EntityHandlerKey`** (new, `RayTree.Core.Handling`):

`public readonly record struct EntityHandlerKey(Type EntityType, string HandlerName)` — the
typed dictionary key used by `ChangeSubscriber.IsolatedQueues`. Replaces the anonymous tuple
`(Type, string)` that was used internally.

**`InMemoryBroadcastQueue`** (new, `RayTree.Plugins.InMemory`):

Fan-out in-memory queue for Isolated-mode testing and local development.
`Subscribe()` returns a fresh `IQueueConsumer` backed by its own channel; every call to
`PublishAsync` delivers to all subscribed channels. Disposing a subscriber removes its
channel from the broadcast set.

```csharp
var broadcast = new InMemoryBroadcastQueue();

// Pass as both the publisher target and the consumer factory:
.UsePublisher(broadcast)
.UseConsumerFactory(_ => broadcast.Subscribe())
```

### Breaking Changes

#### `IEntityBuilder<TEntity>` — handler methods removed; `UseConsumer` return type changed

The methods `OnInsert`, `OnUpdate`, `OnDelete`, and `OnChange` are **removed** from
`IEntityBuilder<TEntity>`. Handler registration is only reachable via the post-fork builders
returned by `UseConsumer` or `UseConsumerFactory`.

The return type of `IEntityBuilder<TEntity>.UseConsumer(IQueueConsumer)` changes from
`IEntityBuilder<TEntity>` to `ISharedHandlerBuilder<TEntity>`.

**Migration:** reorder your `ForEntity` call chain so that `UseSerializer`, `UseCompressor`,
and `UseSubscriberOptions` come *before* the `UseConsumer` call (those methods are on
`IEntityBuilder<TEntity>`; `ISharedHandlerBuilder<TEntity>` exposes only handler-registration
methods). Then chain handler registrations on the returned builder:

```csharp
// Before
.ForEntity<Order>(e => e
    .UseConsumer(consumer)
    .UseSerializer(serializer)
    .OnInsert(handler))

// After
.ForEntity<Order>(e => e
    .UseSerializer(serializer)
    .UseConsumer(consumer)
    .OnInsert(handler))
```

#### Known limitation — Shared-mode broker ACK ordering

`RabbitMqConsumer` and `KafkaConsumer` ACK / commit the broker delivery **before** the
subscriber processes the message. In Shared mode this means broker-driven redelivery does not
fire even when the dedup mark is reverted on handler failure. The dedup-revert retry guarantee
is strong with `InMemoryQueue`; for Rabbit/Kafka it is best-effort only. Isolated mode is not
affected (each named handler has its own consumer and ACK lifecycle, and the per-handler dedup
key prevents double-processing across redeliveries). A follow-up change
(`consumer-ack-after-handler`) will fix ACK ordering by adding an explicit ACK callback to
`IQueueConsumer`.

---

## [0.0.9-pre-release]

### Added

#### OpenTelemetry metrics (`RayTree.Core`, `RayTree.OpenTelemetry`)

RayTree now ships a complete, production-ready metrics surface built on the BCL
`System.Diagnostics.Metrics` API. All instruments are emitted through a single
`RayTreeMeter` instance; OTel SDK wiring is provided by the new
`RayTree.OpenTelemetry` peer assembly.

**`RayTree.Core` — instrument layer**

`RayTreeMeter` owns a `System.Diagnostics.Metrics.Meter("RayTree", <version>)`
and the full set of 14 instruments:

| Instrument | Kind | Unit | Source |
|---|---|---|---|
| `raytree.outbox.writes` | Counter | `{writes}` | `EntityChangeTracker.TrackXxxAsync` |
| `raytree.outbox.messages.published` | Counter | `{messages}` | `OutboxPublisherService` |
| `raytree.outbox.messages.failed` | Counter | `{messages}` | `OutboxPublisherService` |
| `raytree.outbox.records.cleaned` | Counter | `{records}` | `OutboxPublisherService` |
| `raytree.outbox.stale_unpublished.removed` | Counter | `{records}` | `OutboxPublisherService` |
| `raytree.outbox.batch.size` | Histogram | `{messages}` | `OutboxPublisherService` |
| `raytree.outbox.publish.duration` | Histogram | `s` | `OutboxPublisherService` |
| `raytree.outbox.publish.attempts` | Histogram | `{attempts}` | `OutboxPublisherService` |
| `raytree.outbox.lag.duration` | Histogram | `s` | `OutboxPublisherService` |
| `raytree.outbox.payload.size` | Histogram | `By` | `OutboxPublisherService` |
| `raytree.outbox.pending` | ObservableGauge | `{messages}` | `RayTreeMeter` |
| `raytree.subscriber.messages.processed` | Counter | `{messages}` | `ChangeSubscriber` |
| `raytree.subscriber.messages.deduplicated` | Counter | `{messages}` | `ChangeSubscriber` |
| `raytree.subscriber.messages.skipped` | Counter | `{messages}` | `ChangeSubscriber` |
| `raytree.subscriber.handler.failures` | Counter | `{handlers}` | `ChangeSubscriber` |
| `raytree.subscriber.handler.attempts` | Histogram | `{attempts}` | `ChangeSubscriber` |
| `raytree.subscriber.processing.duration` | Histogram | `s` | `ChangeSubscriber` |
| `raytree.subscriber.lag.duration` | Histogram | `s` | `ChangeSubscriber` |

All instruments are tagged with `entity_type`; change-specific instruments add
`change_type`; the skipped-messages counter adds `reason`.

- `raytree.outbox.pending` is an observable gauge: `RegisterPendingGauge(Func<IEnumerable<(Type, IOutbox)>>)`
  registers the callback. Results are cached for `DefaultPendingCacheTtl = 10 s`
  (configurable via the `RayTreeMeter(TimeSpan pendingCacheTtl)` constructor
  overload; pass `TimeSpan.Zero` to disable caching). This bounds DB round-trips
  to at most one query per outbox per cache window, even with sub-second OTel
  collection intervals.

- `RayTreeMeter.MeterName` is a public constant (`"RayTree"`) for use in custom
  OTel views and filters.

**Builder and DI integration**

- `ChangeTrackingBuilder` creates a `RayTreeMeter` automatically when the caller
  does not supply one.
- `UseMeter(RayTreeMeter)` on `IChangeTrackingBuilder` accepts a caller-owned
  meter. The tracker tracks ownership via an `ownsMeter` flag: auto-created meters
  are disposed with the tracker; caller-supplied meters are left alone.
- `EntityChangeTracker.Meter` exposes the meter so callers can inspect or share it.
- `AddChangeTracking` (Generic Host) registers `RayTreeMeter` as a DI singleton and
  feeds it back into the builder via `UseMeter`, so custom instrumentation code can
  inject `RayTreeMeter` directly.

**`RayTree.OpenTelemetry` — OTel SDK peer assembly**

New assembly with zero production dependency on `RayTree.Core` beyond the meter
name constant. `RayTree.Core` and `RayTree.Hosting` continue to depend only on the
BCL (`System.Diagnostics.Metrics`) — applications that do not pull in
`RayTree.OpenTelemetry` receive zero transitive OTel dependencies.

```csharp
services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddRayTreeMetrics()     // ← the only call needed
        .AddPrometheusExporter());
```

- `RayTreeInstrumentation` — `public static class` exposing
  `public const string MeterName = "RayTree"`. Use this in custom OTel views
  instead of hard-coding the literal.
- `MeterProviderBuilderExtensions.AddRayTreeMetrics` — thin `AddMeter(MeterName)`
  pass-through. Does not configure exporters, views, or histogram bucket
  boundaries; callers retain full control.

#### Documentation

- New `docs/opentelemetry-metrics.md`: full instrument inventory with tags and
  units, Generic Host and standalone `MeterListener` wire-up examples, pending-gauge
  cache behaviour, suggested histogram bucket boundaries for 8 instruments, sample
  Prometheus queries (throughput, tail latency, backlog alert, retry shape), and
  `UseMeter` injection example.
- `docs/README.md`: OpenTelemetry Metrics added to the Features list.
- `docs/configuration.md`: "Observability — OpenTelemetry Metrics" section with
  default and custom meter usage.

### Breaking Changes

#### `IOutbox` gains `GetPendingCountAsync`

```csharp
Task<long> GetPendingCountAsync(Type entityType, CancellationToken ct = default);
```

Returns the count of unpublished records for the given entity type. Used by the
`raytree.outbox.pending` observable gauge. External `IOutbox` implementations must
add this method.

#### `ChangePublisher` constructor requires `RayTreeMeter`

Before: `new ChangePublisher(ILoggerFactory)`
After: `new ChangePublisher(ILoggerFactory, RayTreeMeter)`

`RayTreeMeter` is required (non-nullable). The builder layer constructs a default
meter when the caller does not supply one; there is no internal fallback inside
`ChangePublisher`.

#### `OutboxPublisherService` constructor requires `RayTreeMeter`

Before: `new OutboxPublisherService(ChangePublisher, Type, OutboxPublisherOptions, ILoggerFactory)`
After: `new OutboxPublisherService(ChangePublisher, Type, OutboxPublisherOptions, ILoggerFactory, RayTreeMeter)`

#### `ChangeSubscriber` constructor requires `RayTreeMeter`

Before: `new ChangeSubscriber(ILogger<ChangeSubscriber>, IDeduplicationStore?, SubscriberOptions?)`
After: `new ChangeSubscriber(ILogger<ChangeSubscriber>, RayTreeMeter, IDeduplicationStore?, SubscriberOptions?)`

### Tests

- `OutboxPublisherServiceMetricsTests` — new test: `PublishWithRetry_OnExhaustion_RecordsAttemptsAndFailureDurations`
  verifies that `raytree.outbox.publish.attempts` and `raytree.outbox.publish.duration`
  are both recorded when all retry attempts are exhausted.
- `ChangeSubscriberMetricsTests` — new test: `ProcessMessageAsync_HandlerAlwaysFails_RecordsAttemptsFailuresAndProcessingDurations`
  verifies `raytree.subscriber.handler.attempts`, `raytree.subscriber.handler.failures`,
  and `raytree.subscriber.processing.duration` on full retry exhaustion.
- `RayTreeMeterPendingGaugeCacheTests` (3 tests) — pins the pending-gauge cache contract:
  two observations within TTL hit the outbox once; `TimeSpan.Zero` disables the cache;
  TTL expiry triggers a re-poll.
- `UseMeterOwnershipTests` (2 tests) — proves that a caller-supplied meter is not
  disposed when the tracker is disposed, and that a builder-created meter is.
- `RayTreeMeterEndToEndTests` (3 tests, `RayTree.OpenTelemetry.Tests`) — end-to-end
  OTel SDK pipeline tests: real instruments flow through `AddRayTreeMetrics` and a
  `BaseExporter<Metric>`; all instrument names pass the Prometheus naming validation;
  unit metadata (`s`, `By`) survives the pipeline.

#### `RabbitMqConsumer` constructor simplified

`RabbitMqConsumer` no longer accepts an `ILoggerFactory` parameter. Message-receive
errors now silently nack and requeue without a log entry; the consumer's internal
channel handles retries at the broker level. Builder call-sites using
`UseRabbitMq(configure)` are unaffected — the extension method was updated in
parallel. Direct construction is affected:

Before: `new RabbitMqConsumer(options, loggerFactory)`
After: `new RabbitMqConsumer(options)`

### Removed

#### `RayTree.Plugins.Serializers.MsgPack` project deleted

The `RayTree.Plugins.Serializers.MsgPack` assembly was a duplicate of
`RayTree.Plugins.Serializers.MessagePack` (different namespace, identical
implementation). It has been removed. Use `RayTree.Plugins.Serializers.MessagePack`
(`MessagePackSerializerPlugin` in the `RayTree.Plugins.Serializers.MessagePack`
namespace) instead.

### Fixed

- `GetPendingCountAsync_OnEmptyTable_ReturnsZero` (PostgreSQL integration test) was
  returning 3 instead of 0 because the shared `pending_count_outbox` table was not
  cleaned up between tests. Added `[TearDown]` with `TRUNCATE TABLE` to isolate
  each test.

### Infrastructure

#### Solution format migrated to `.slnx`

`RayTree.sln` has been replaced by `RayTree.slnx` (the new XML-based Visual Studio
solution format). No project files changed; only the solution container format
updated. CI pipelines reference `RayTree.slnx`. If you have local shell scripts or
IDE settings pointing at `RayTree.sln`, update them to `RayTree.slnx`.

#### Security policy (`SECURITY.md`)

A `SECURITY.md` file has been added at the repo root. It documents the supported
version policy (latest only) and the responsible-disclosure process via GitHub
Security Advisories.

### Dependencies

| Package | From | To |
|---|---|---|
| `Microsoft.Extensions.Logging.Abstractions` | `10.0.7` | `10.0.8` |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | `10.0.7` | `10.0.8` |
| `Microsoft.EntityFrameworkCore` | `10.0.7` | `10.0.8` |
| `Microsoft.EntityFrameworkCore.InMemory` | `10.0.7` | `10.0.8` |
| `Microsoft.Extensions.Hosting.Abstractions` | — | `10.0.8` (new — replaces full `Hosting`) |
| `Microsoft.Extensions.Options.ConfigurationExtensions` | — | `10.0.8` (new — replaces `Configuration.Binder`) |
| `OpenTelemetry` | — | `1.15.3` (new — `RayTree.OpenTelemetry` only) |
| `OpenTelemetry.Api` | — | `1.15.3` (new — `RayTree.OpenTelemetry` only) |
| `Microsoft.Extensions.Hosting` | `10.0.7` | removed (replaced by `Hosting.Abstractions`) |
| `Microsoft.Extensions.Configuration.Binder` | `10.0.7` | removed (replaced by `Options.ConfigurationExtensions`) |
| `Microsoft.EntityFrameworkCore.Relational` | `10.0.7` | removed (unused direct reference) |
| `Microsoft.Extensions.DependencyInjection` | `10.0.7` | removed (unused direct reference) |

---

## [0.0.8-pre-release]

### Breaking Changes

#### `UseQueue` renamed to `UsePublisher` and `UseConsumer`

The `UseQueue` method was overloaded for both publisher and subscriber contexts, making intent ambiguous. It has been split into two purpose-named methods:

| Before | After | Side |
|---|---|---|
| `IChangeTrackingBuilder.UseQueue<T>(factory)` | `UsePublisher<T>(factory)` | Publisher — global |
| `IChangePublisherBuilder.UseQueue<T>(factory)` | `UsePublisher<T>(factory)` | Publisher — global |
| `IEntityBuilder<TEntity>.UseQueue(IQueuePublisher)` | `UsePublisher(IQueuePublisher)` | Publisher — per-entity |
| `IEntityPublisherBuilder<TEntity>.UseQueue(IQueuePublisher)` | `UsePublisher(IQueuePublisher)` | Publisher — per-entity |
| `IEntitySubscriberBuilder<TEntity>.UseQueue(IQueueConsumer)` | `UseConsumer(IQueueConsumer)` | Subscriber — per-entity |
| `IEntityBuilder<TEntity>.UseConsumer(IQueueConsumer)` → internal call | now calls `UseConsumer` on subscriber builder | Subscriber — per-entity |

**Migration:** rename call-sites as shown in the table. No behaviour changes — only the method names changed.

**Plugin extensions updated to match:**
- `InMemoryBuilderExtensions` — internal `e.UseQueue(new InMemoryQueue())` calls updated to `e.UsePublisher(new InMemoryQueue())`
- `InMemorySubscriberExtensions` — delegates to `UseConsumer` instead of `UseQueue`
- `KafkaBuilderExtensions` — calls `UsePublisher<IQueuePublisher>(...)` instead of `UseQueue`
- `KafkaSubscriberExtensions` — calls `UseConsumer(...)` instead of `UseQueue`
- `RabbitMqBuilderExtensions` — calls `UsePublisher<IQueuePublisher>(...)` instead of `UseQueue`
- `RabbitMqSubscriberExtensions` — calls `UseConsumer(...)` instead of `UseQueue`

---

## [0.0.7-pre-release]

### Added

#### Automatic schema migration in `PostgreSqlOutbox<TEntity>` and `PostgreSqlRepository<TEntity>`

Both classes now manage their PostgreSQL schema automatically on every `InitializeAsync` call — no `AutoMigrate` flag, always on.

**Fresh table path** — when the table does not yet exist, a single `CREATE TABLE IF NOT EXISTS` statement creates all columns and indexes in one round-trip. The `IF NOT EXISTS` guard is kept as a concurrency safety net (e.g. two processes starting simultaneously).

**Existing table path** — when the table already exists:

- **Column diff** (`SchemaMigrator`): each desired column (entity property columns for outbox; key columns for repository) is compared against `information_schema.columns`. Missing columns are added via `ALTER TABLE … ADD COLUMN IF NOT EXISTS`. Adding a `NOT NULL` column without a default to a table that already has rows throws `InvalidOperationException` with a descriptive message (fail-fast — the developer must add a `DEFAULT` or migrate manually). Columns present in the database but not in the entity schema log a `Warning` ("consider dropping it manually"). Columns whose type differs from the expected type log a `Warning` ("type changes must be migrated manually").
- **Index diff** (`IndexMigrator`): each desired index is compared against live `pg_index` catalog data (uniqueness, ordered column list, WHERE predicate). Indexes that do not exist are created. Indexes whose definition has changed (any of: uniqueness, column order, WHERE clause) are dropped (`DROP INDEX IF EXISTS public.{name}`) and recreated. Indexes that exist in the database but are not in the entity schema log a `Warning` ("consider dropping it manually"). WHERE clause comparison is case-insensitive and trimmed so `published = FALSE` (application) matches `published = false` (PostgreSQL catalog).

New internal infrastructure supporting the above:

| Class | Role |
|---|---|
| `SchemaInspector` | Static helper — `TableExistsAsync`, `GetColumnsAsync` (queries `information_schema.columns`), `GetIndexesAsync` (queries `pg_index` catalog, returns ordered columns via `unnest(indkey::smallint[]) WITH ORDINALITY`, WHERE via `pg_get_expr`), `ExecuteDdlAsync`, `TableHasRowsAsync` |
| `SchemaMigrator` | Column diff logic — parameterised by a `generateAddColumn` delegate and an `isOrphanCandidate` predicate so it is reusable by both outbox and repository |
| `IndexMigrator` | Index diff logic — `ApplyDiffAsync` with DROP+CREATE on mismatch, `Matches()` for definition comparison |
| `PostgreSqlTypeNormalizer` | Maps `information_schema` type fields to canonical DDL strings (e.g. `character varying` + max-length → `VARCHAR(n)`, `ARRAY` + udt_name → `element_type[]`) |

#### `ILoggerFactory` required constructor parameters

`PostgreSqlOutbox<TEntity>` and `PostgreSqlRepository<TEntity>` now require `ILoggerFactory` as their second constructor parameter — following the same pattern as `KafkaConsumer` and `RabbitMqConsumer`. Logging is used for schema migration diagnostics (column/index added at `Information`; orphan column/index and type mismatch at `Warning`). Builder extension methods (`UsePostgreSqlOutbox`, `UsePostgreSqlRepository`) accept an optional `ILoggerFactory? loggerFactory = null` parameter that defaults to `NullLoggerFactory.Instance` when omitted, so existing builder call-sites compile without change.

### Breaking Changes

- `PostgreSqlOutbox<TEntity>` constructor: `ILoggerFactory` is now required as the second parameter.
  Before: `new PostgreSqlOutbox<TEntity>(options)`
  After: `new PostgreSqlOutbox<TEntity>(options, loggerFactory)`
  Builder call-sites are unaffected (the extension method absorbs the default).

- `PostgreSqlRepository<TEntity>` constructor: `ILoggerFactory` is now required as the second parameter.
  Before: `new PostgreSqlRepository<TEntity>(options)`
  After: `new PostgreSqlRepository<TEntity>(options, loggerFactory)`
  Builder call-sites are unaffected.

---

## [0.0.6-pre-release]

### Fixed

#### Duplicate publish prevention in `NotificationBasedPublisher`

- `OutboxPublisherService` was ignoring `OutboxPublisherOptions.UseNotificationChannel`
  and always polling at `PollingInterval` (default 5 s), racing with
  `NotificationBasedPublisher` to publish the same record. When
  `UseNotificationChannel = true` the service now uses `FallbackPollingInterval`
  instead, demoting itself to a safety-net role while `NotificationBasedPublisher`
  handles normal delivery.
- `NotificationBasedPublisher.FallbackPollingLoopAsync` was running on every tick
  unconditionally, publishing every change a second time in parallel with the
  `OnNotification` fast-path. The loop now polls only on the first tick at startup
  (to drain records written before the listener was established) and when the LISTEN
  connection is unhealthy (`_listenerHealthy = false`).
- `OnNotification` had a TOCTOU race: it read `change.Published`, then raced to
  publish before calling `MarkPublishedAsync`, allowing two concurrent publishers to
  both publish the same record. `OnNotification` and `ProcessUnpublishedChangesAsync`
  now atomically claim records via `IOutbox.TryClaimForPublishingAsync` before
  publishing. On publish failure the claim is reverted via `IOutbox.RevertClaimAsync`
  so the fallback loop can retry.

#### Deduplication correctness in `ChangeSubscriber`

- `ChangeSubscriber` marked a message's `CorrelationId` as processed **before**
  invoking handlers. When a handler exhausted retries and threw (`SkipOnFailure =
  false`), the correlation ID remained in the store. With a persistent dedup store
  (e.g. Redis), the message broker's redelivered copy would then be silently dropped
  forever. The subscriber now calls `IDeduplicationStore.RevertProcessedAsync` before
  rethrowing, so the redelivered message is accepted and retried.
- `IDeduplicationStore.CleanupAsync` and `SubscriberOptions.DeduplicationRetention`
  existed but were never wired up, causing `InMemoryDeduplicationStore` to grow
  without bound. `ChangeSubscriber` now calls `MaybeDedupCleanupAsync` after each
  successfully processed message, gated by the new
  `SubscriberOptions.DeduplicationCleanupInterval` (default 1 h).
- `IDeduplicationStore.IsProcessedAsync` was defined on the interface and implemented
  but never called anywhere. Removed (breaking change for external implementations —
  delete the method).

#### Ordering defaults for `MaxPublishConcurrency` and `MaxDegreeOfParallelism`

- `OutboxPublisherOptions.MaxPublishConcurrency`, `NotificationBasedPublisherOptions.MaxPublishConcurrency`,
  and `SubscriberOptions.MaxDegreeOfParallelism` were all introduced with a default of
  `Environment.ProcessorCount`. Concurrent publishing enqueues messages in non-deterministic
  order, breaking per-partition ordering guarantees; `TrackMultiple_AllChangesDeliveredInOrder`
  (Kafka) failed non-deterministically as a result. All three default to `1` (sequential).
  Increase explicitly when ordering is not required.

### Added

- `IOutbox.TryClaimForPublishingAsync(long id, CancellationToken)` — atomically
  transitions a record from `published = FALSE` to `published = TRUE` and returns
  `true` if this caller made the transition (i.e. the record was unpublished).
  Returns `false` when another publisher already claimed it.
- `IOutbox.RevertClaimAsync(long id, CancellationToken)` — sets `published = FALSE`,
  undoing a claim after a publish failure so the record remains visible to the
  fallback polling loop.
- `IDeduplicationStore.RevertProcessedAsync(string correlationId, CancellationToken)`
  — removes a correlation ID from the store so a redelivered message can be retried
  after a handler failure.
- `SubscriberOptions.DeduplicationCleanupInterval` (default 1 h) — how often
  `ChangeSubscriber` triggers `IDeduplicationStore.CleanupAsync` to evict entries
  older than `DeduplicationRetention`.

#### High-load throughput improvements

- `OutboxPublisherOptions.MaxPublishConcurrency` (default 1 — sequential) —
  `OutboxPublisherService.ProcessBatchAsync` now uses `Parallel.ForEachAsync` bounded
  by this option. Default is 1 to preserve per-partition message ordering; increase
  explicitly when ordering is not required and throughput matters more.
- `OutboxPublisherService` skips the inter-batch sleep when the batch was full
  (`changes.Count == BatchSize`), draining a backlog immediately rather than waiting
  one full `PollingInterval` between each batch.
- `SubscriberOptions.MaxDegreeOfParallelism` (default 1) — `ConsumeFromConsumerAsync`
  and `ConsumeFromQueueAsync` now use `Parallel.ForEachAsync` bounded by this option.
  Default is 1 (sequential) to preserve per-partition message ordering (e.g. Kafka);
  increase explicitly when handlers are order-independent and throughput matters more.
- `NotificationBasedPublisherOptions.MaxConcurrentNotifications` (default 16) —
  `OnNotification` is now bounded by a `SemaphoreSlim`; notifications that arrive
  while at capacity are dropped and will be delivered by the fallback polling loop.
- `NotificationBasedPublisherOptions.MaxPublishConcurrency` (default 1 — sequential)
  — `ProcessUnpublishedChangesAsync` uses `Parallel.ForEachAsync` bounded by this
  option. Same ordering rationale as `OutboxPublisherOptions.MaxPublishConcurrency`.

#### Logging improvements

- `NotificationBasedPublisher`: LISTEN connection loss now logs at `Warning` only
  on the first unhealthy tick (suppressed on subsequent ticks while still unhealthy);
  recovery logs at `Information` so operators can confirm the fast-path is restored.
- `NotificationBasedPublisher.OnNotification`: logs at `Debug` when
  `TryClaimForPublishingAsync` returns `false` (record already claimed by another
  publisher), making claim contention visible under high load.
- `ChangeSubscriber.ProcessMessageAsync`: logs successful message dispatch at `Debug`
  and dedup-mark revert (handler exhausted all retries, `SkipOnFailure = false`) at
  `Warning` before rethrowing, so operators can correlate repeated handler failures
  with redelivery.
- `ChangeTrackingHostedService`: consumer loop start log now includes the entity type
  name — e.g. `"Starting change tracking consumer loop for OrderEntity (1 of 3)"`.

### Breaking Changes

- `IOutbox` gains two new methods: `TryClaimForPublishingAsync` and `RevertClaimAsync`.
  External implementations must add both.
- `IDeduplicationStore` loses `IsProcessedAsync` and gains `RevertProcessedAsync`.
  External implementations must remove the former and add the latter.

---

## [0.0.5-pre-release]

### Added

#### Primitive array support for PostgreSQL outbox and repository

- 1D arrays of primitive types are now stored as native PostgreSQL array columns.
  `EntityColumnMapper.ToPostgresType` maps `T[]` to the corresponding `PG_TYPE[]`:

  | C# type | PostgreSQL column |
  |---|---|
  | `int[]` | `INTEGER[]` |
  | `long[]` | `BIGINT[]` |
  | `short[]` / `byte[]` / `sbyte[]` | `SMALLINT[]` |
  | `float[]` | `REAL[]` |
  | `double[]` | `DOUBLE PRECISION[]` |
  | `decimal[]` | `NUMERIC[]` |
  | `bool[]` | `BOOLEAN[]` |
  | `Guid[]` | `UUID[]` |
  | `DateTime[]` / `DateTimeOffset[]` | `TIMESTAMPTZ[]` |
  | `string[]` | `TEXT[]` |

- Nullable-element arrays (e.g. `int?[]`) strip the nullable wrapper before mapping
  the element type — the column type is the same as for a non-nullable element array.
- Multi-dimensional arrays are not supported; use `[Column(TypeName = "...")]` to
  declare the column type explicitly when needed.
- New `EntityColumnMapper.ConvertFromDb(object value, Type targetType)` helper is
  used by both `PostgreSqlOutbox.ReadEntityChange` and `PostgreSqlRepository.MapEntity`
  to read values back. It checks assignability first (Npgsql returns the correct CLR
  array type natively) and falls back to `Convert.ChangeType` for scalar numeric
  coercions.

#### Documentation

- `docs/database-migration.md` C# → PostgreSQL type mapping table extended with
  array types and array-specific rules.

### Fixed

- `OutboxPublisherService.MaybeRunCleanupAsync` previously advanced `_lastCleanup`
  before running the cleanup operations, so a transient DB failure would still delay
  the next retry by a full `CleanupInterval`. The timestamp is now only advanced when
  both operations complete successfully; a failure leaves the timer unchanged so the
  next poll tick retries immediately.
- `PostgreSqlOutbox.BatchDeleteAsync` was allocating a new `NpgsqlCommand` on every
  batch iteration. The command is now created once outside the loop and reused across
  all `ExecuteNonQueryAsync` calls, reducing allocation and repeated query-parse
  overhead on large cleanup runs.

---

## [0.0.4-pre-release]

### Added

#### Outbox rotation integrated into the publisher loop

- `OutboxPublisherService` now runs `MaybeRunCleanupAsync` after every poll batch.
  Rotation fires eagerly on the first tick (so stale rows from before a restart are
  cleaned up immediately), then respects `OutboxPublisherOptions.CleanupInterval`
  for subsequent runs.
- Cleanup errors are isolated in their own `try/catch`; a transient DB failure logs
  an error but does not abort the publish loop.
- No separate hosted service or external scheduler is needed — rotation is tied to
  the existing publisher lifetime.

#### Stale unpublished record cleanup

- New `IOutbox.CleanupStaleUnpublishedAsync(TimeSpan staleThreshold, CancellationToken)`
  method. When records have been in the outbox without being published for longer
  than `staleThreshold`, they are deleted. A `Warning` log is emitted whenever any
  are found — treat this as an operator signal for queue health issues.
- Implemented in `InMemoryOutbox` and `PostgreSqlOutbox<TEntity>`.
- Opt-in via `OutboxPublisherOptions.StaleUnpublishedThreshold` (default `null` —
  disabled).

#### New `OutboxPublisherOptions` rotation properties

| Property | Default | Description |
|---|---|---|
| `CleanupRetentionPeriod` | 7 days | Minimum age of a published row before it is deleted. |
| `CleanupInterval` | 1 hour | How often rotation runs; first tick is always immediate. |
| `StaleUnpublishedThreshold` | `null` | When set, unpublished rows older than this are also removed. |

#### PostgreSQL batched cleanup

- `PostgreSqlOutbox.CleanupPublishedAsync` and `CleanupStaleUnpublishedAsync` now
  delete in batches using `DELETE … WHERE id IN (SELECT id … ORDER BY id LIMIT
  @BatchSize)` loops. This avoids large single-statement locks and WAL spikes on
  busy tables.
- `PostgreSqlOutboxOptions.CleanupBatchSize` (default `1000`) controls the rows
  deleted per statement.

#### New PostgreSQL partial index

- `idx_*_outbox_cleanup` — `(timestamp) WHERE published = TRUE` — added to the
  schema so `CleanupPublishedAsync` uses an index scan instead of a sequential scan.
  Created via `CREATE INDEX IF NOT EXISTS`, so existing tables pick it up on next
  startup.

#### Documentation

- New **Outbox rotation** section in `docs/README.md` covering configuration,
  `appsettings.json` binding, batch size tuning, log levels, and manual rotation
  via `OutboxCleanupService`.

### Breaking Changes

- `IOutbox` gains a new method `CleanupStaleUnpublishedAsync(TimeSpan staleThreshold, CancellationToken)`.
  Any external implementation of `IOutbox` must add this method. The built-in implementations
  (`InMemoryOutbox`, `PostgreSqlOutbox<TEntity>`) are updated automatically.

### Fixed

- `ServiceCollectionExtensions` was passing `options.PollingInterval * 10` (50 s by
  default) as the retention period for `OutboxCleanupService`. It now correctly uses
  `options.CleanupRetentionPeriod` (default 7 days).
- PostgreSQL integration tests no longer race with the background `OutboxPublisherService`
  poll loop. The outbox is created directly in `SetUp` without starting a publisher,
  eliminating a non-deterministic failure where the poller marked records as
  published between two `WriteAsync` calls in the same test.

### Dependencies

| Package | From | To |
|---|---|---|
| `Microsoft.EntityFrameworkCore` | `8.0.26` | `10.0.7` |
| `Microsoft.EntityFrameworkCore.InMemory` | `8.0.26` | `10.0.7` |
| `Microsoft.EntityFrameworkCore.Relational` | `8.0.11` | `10.0.7` |
| `Microsoft.Extensions.DependencyInjection` | `8.0.1` | `10.0.7` |
| `Microsoft.Extensions.Hosting` | `8.0.1` | `10.0.7` |
| `Microsoft.Extensions.Configuration.Binder` | `8.0.2` | `10.0.7` |
| `Npgsql.EntityFrameworkCore.PostgreSQL` | `8.0.11` | `10.0.1` |
| `Microsoft.Extensions.Options` | `8.0.2` | removed (unused) |
| `System.IO.Pipelines` | `8.0.0` | removed (unused) |
| `StackExchange.Redis` | `2.8.16` | removed (no implementation) |

---

## [0.0.3-pre-release]

### Added

#### `[Key]` primary key support (`RayTree.Plugins.PostgreSQL`, `RayTree.Plugins.InMemory`)

- `EntityColumnMapper.GetKeyProperties(Type)` resolves the ordered list of key
  properties for an entity type. Looks for `[Key]`-annotated properties first;
  multiple `[Key]` properties form a composite key ordered by `[Column(Order = n)]`
  then by declaration order. Falls back to the `Id` convention property when no
  `[Key]` is present. Throws `InvalidOperationException` at construction time
  (fail-fast) when neither exists.
- `PostgreSqlRepository<TEntity>` uses key properties to build typed `INSERT`,
  `WHERE` (for `UpdateAsync` / `DeleteAsync` / `GetByIdAsync`), and the source-table
  `UNIQUE` index. SQL is constructed once at construction time and reused for every
  call.
- `SourceTableDdlGenerator.CreateDefault` accepts `IReadOnlyList<SourceTableColumn>
  keyColumns`; key columns are appended after the infrastructure columns
  (`id`, `created_at`, `updated_at`, `version`) and a `UNIQUE` index is added
  covering them.
- `InMemoryRepository<TEntity>` gained full `[Key]` / composite-key / `Id`
  convention resolution, mirroring `EntityColumnMapper.GetKeyProperties`. Composite
  keys are serialised with a `\0` null-character separator to prevent ambiguous
  collisions between values that contain common delimiter characters.

#### DataAnnotations attribute support for PostgreSQL schema generation

- `[NotMapped]` — property excluded from the outbox schema entirely.
- `[Column("name")]` — overrides the column name suffix; the `state_` prefix is
  always kept to prevent collisions with fixed outbox metadata columns.
- `[Column(TypeName = "...")]` — sets the PostgreSQL type verbatim (e.g. `JSONB`,
  `NUMERIC(18,4)`).
- `[Required]` — emits `NOT NULL` on reference-type and nullable value-type
  properties.
- `[MaxLength(n)]` / `[StringLength(n)]` — emits `VARCHAR(n)` instead of `TEXT`
  for string properties (ignored when `TypeName` is already set).
- `[Table("name")]` — used as the base name when deriving default outbox and source
  table names. `EntityColumnMapper.GetTableName(Type)` encapsulates this logic;
  both `PostgreSqlOutbox` and `PostgreSqlRepository` use it for their defaults.

#### `IRepository<TEntity>` breaking change

- `GetByIdAsync(object id, ...)` → `GetByIdAsync(object[] keyValues, ...)`.
  Accepts one value per key property in the same order as declared. Both
  `PostgreSqlRepository` and `InMemoryRepository` validate the count at call time
  and throw `ArgumentException` if it does not match.

### Changed

- `PostgreSqlRepository<TEntity>` constructor now builds a complete
  `columnName → PropertyInfo` cache from `EntityColumnMapper.GetColumns` at startup.
  `MapEntity` is a pure dictionary lookup with no per-row reflection calls.
- `PostgreSqlOutbox<TEntity>` default table name derived via
  `EntityColumnMapper.GetTableName` + `"_outbox"` instead of a hand-rolled
  snake-case helper.
- Target framework upgraded from `net8.0` to `net10.0`.

### Fixed

- `InMemoryRepository` composite key separator changed from `|` to `\0`
  (null character), preventing key collisions when entity property values contain
  the pipe character.

### Tests

- `GetKeyPropertiesTests` — 9 unit tests covering single `[Key]`, `Id` fallback,
  composite key with `[Column(Order)]`, composite key without `[Column(Order)]`
  (declaration-order fallback), no-key throws, constructor fail-fast, and wrong
  key count.
- `InMemoryRepositoryTests` — wrong key count throws `ArgumentException`.
- `InMemoryRepositoryCompositeKeyTests` — null-char separator correctness: keys
  `("a|b", "c")` and `("a", "b|c")` remain distinct.
- `DefaultSourceTableNameIntegrationTests` — integration test verifying that
  omitting `TableName` in `PostgreSqlRepositoryOptions` creates a table named after
  the entity type.
- `PostgresContainerFactory` extracted as a shared helper used by all PostgreSQL
  integration tests.

### Dependencies

| Package | From | To |
|---|---|---|
| NUnit | — | updated |
| MessagePack | — | updated |
| Npgsql | — | updated |
| Kafka client | — | updated |
| Entity Framework Core | — | updated |
| RabbitMQ.Client | — | updated |
| Testcontainers.PostgreSql | — | updated |
| Testcontainers.RabbitMq | — | updated |

---

## [0.0.2] — 2026-05-09

### Added

- Structured logging via `Microsoft.Extensions.Logging` throughout. Pass an
  `ILoggerFactory` to `ChangeTrackingBuilder` or let `AddChangeTracking` wire it
  from DI automatically. Defaults to `NullLoggerFactory` so existing call-sites
  continue to compile without change.
- `ILoggerFactory` parameter on `ChangeTrackingBuilder`, `KafkaConsumer`,
  `RabbitMqConsumer`, and `ChangeTrackingHostedService` constructors.

---

## [0.0.1] — initial release

- Initial implementation of entity change tracking with outbox pattern, in-memory
  and PostgreSQL plugins, Kafka and RabbitMQ queue plugins, JSON / MessagePack /
  Protobuf serialisers, Gzip / Brotli / LZ4 compressors, EF Core interceptor,
  and ASP.NET Core hosting integration.
