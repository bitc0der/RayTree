# Architecture review — performance, concurrency, data access, multithreading

Ranked by criticality. Each task: implement → run relevant tests → commit → move to next.

## Critical

- [x] **1. `EntityChangeInterceptor.CreateChange` — fully uncached reflection per changed entity per `SaveChanges`**
  [src/RayTree.EntityFrameworkCore/Interceptors/EntityChangeInterceptor.cs:104-110](src/RayTree.EntityFrameworkCore/Interceptors/EntityChangeInterceptor.cs#L104-L110)
  On every `SaveChanges`/`SaveChangesAsync`, for every changed entity: `typeof(EntityChange<>).MakeGenericType(entityType)` + `Activator.CreateInstance` + `GetProperty("State")!.SetValue(...)`. Hottest reflection cost for EF Core users; not even the `MethodInfo` is cached here, unlike every other reflection-dispatch site in the codebase.
  Fix: cache a compiled factory delegate per entity type, e.g. `ConcurrentDictionary<Type, Func<EntityEntry, ChangeType, EntityChange>>` built via `Expression.Lambda`.

- [x] **2. Shutdown race in `NotificationBasedPublisher` — `ObjectDisposedException` in an unobserved background task**
  [src/RayTree.Plugins.PostgreSQL/Outbox/Notification/NotificationBasedPublisher.cs:230-299](src/RayTree.Plugins.PostgreSQL/Outbox/Notification/NotificationBasedPublisher.cs#L230-L299)
  `OnNotification` fires `Task.Run(async () => {...})` per notification, never tracked or awaited. `StopAsync` (lines 81-94) only awaits `_listenTask`/`_fallbackTask`; `Dispose()` (lines 479-484) then disposes `_notificationSemaphore` immediately after. An in-flight notification task's `finally { _notificationSemaphore.Release(); }` throws `ObjectDisposedException` into an unobserved task if it's still running at that point — silent in .NET Core (no process crash) but a real bug: nondeterministic log noise / lost diagnostics during graceful shutdown under load (rolling restarts).
  Fix: track in-flight notification tasks (e.g. a counter + `SemaphoreSlim` barrier, or a `ConcurrentDictionary<Task>` drained via `Task.WhenAll`) and await them in `StopAsync` before disposing the semaphore.

- [x] **3. PostgreSQL: new connection per call across the whole data-access layer**
  [src/RayTree.Plugins.PostgreSQL/Outbox/PostgreSqlOutbox.cs](src/RayTree.Plugins.PostgreSQL/Outbox/PostgreSqlOutbox.cs) (`WriteAsync`, `GetUnpublishedAsync`, `MarkPublishedAsync`, `MarkPublishedBatchAsync`, `TryClaimForPublishingAsync`, `RevertClaimAsync`, `BatchDeleteAsync`, `GetPendingCountAsync`, `GetByIdAsync`, `ExecuteNonQueryAsync`) and [src/RayTree.Plugins.PostgreSQL/Repository/PostgreSqlRepository.cs:122,134,147,165](src/RayTree.Plugins.PostgreSQL/Repository/PostgreSqlRepository.cs#L122) each do `new NpgsqlConnection(...)` + `OpenAsync`. Npgsql pools physical sockets underneath, but this forgoes `NpgsqlDataSource`'s prepared-statement/command caching on every single change write/read in the system.
  Fix: switch to `NpgsqlDataSource` (Npgsql 7+), created once in the constructor; use `dataSource.CreateCommand(...)` instead of `new NpgsqlConnection` per call.

## High

- [x] **4. Asymmetric initialization: `ChangePublisher` sequential vs `ChangeSubscriber` parallel**
  [src/RayTree.Core/Distribution/ChangePublisher.cs:72-91](src/RayTree.Core/Distribution/ChangePublisher.cs#L72-L91)
  `ChangePublisher.InitializeAsync` initializes repositories → outboxes → publishers with a sequential `foreach + await`, and each entity type's `OutboxPublisherService.StartAsync` is also started one at a time. `ChangeSubscriber.InitializeAsync` is documented (CLAUDE.md) as deliberately parallelized via `Task.WhenAll` so one slow consumer doesn't block the others. The publisher side has the identical problem un-fixed: one slow Postgres schema migration for one entity type (e.g. a locking `ALTER TABLE`) delays startup for every other entity type.
  Fix: apply the same `Task.WhenAll` pattern already used on the subscriber side.

- [ ] **5. `_trackedEntityTypes.Contains(entityType)` — O(n) scan per tracked entry per `SaveChanges`**
  [src/RayTree.EntityFrameworkCore/Interceptors/EntityChangeInterceptor.cs:14,84](src/RayTree.EntityFrameworkCore/Interceptors/EntityChangeInterceptor.cs#L84)
  Field is `IEnumerable<Type>`; `.Contains()` linearly scans it for every `ChangeTracker.Entries()` row on every `SaveChanges`.
  Fix: `HashSet<Type>`.

- [ ] **6. Sequential outbox writes when `SaveChanges` touches multiple entity types**
  [src/RayTree.EntityFrameworkCore/Interceptors/EntityChangeInterceptor.cs:123-132](src/RayTree.EntityFrameworkCore/Interceptors/EntityChangeInterceptor.cs#L123-L132)
  `foreach` with `await WriteTypedAsync` one at a time — a `SaveChanges` touching 20 entities across several types does 20 sequential DB round trips (compounds with #3).
  Fix: write in parallel (`Task.WhenAll`), at minimum across distinct entity types.

- [ ] **7. Per-message allocation in `ChangeSubscriber` dispatch**
  [src/RayTree.Core/Handling/ChangeSubscriber.cs:390-392,476-478](src/RayTree.Core/Handling/ChangeSubscriber.cs#L390-L392)
  `handlers.Where(h => h.ChangeType == envelope.ChangeType).ToList()` in both shared and isolated mode — allocates an iterator + `List<>` on every single message processed.
  Fix: a `for` loop into a reused buffer, or group handlers by `ChangeType` at registration time (`Dictionary<ChangeType, List<HandlerRegistration>>`).

## Medium

- [ ] **8. `PostgreSqlRepository` doesn't reuse the existing compiled-setter cache**
  [src/RayTree.Plugins.PostgreSQL/Repository/PostgreSqlRepository.cs:180,195](src/RayTree.Plugins.PostgreSQL/Repository/PostgreSqlRepository.cs#L180)
  `AddKeyParameters`/`MapEntity` use raw `PropertyInfo.GetValue`/`SetValue`, even though `EntityColumnMapper.SetValue` (compiled delegate cache) already exists and is used by `PostgreSqlOutbox.ReadEntityChange`.
  Fix: route through the same `EntityColumnMapper` helper.

- [ ] **9. `Enum.Parse<ChangeType>` on 3 hot paths**
  [src/RayTree.Plugins.Kafka/KafkaConsumer.cs:431](src/RayTree.Plugins.Kafka/KafkaConsumer.cs#L431), [src/RayTree.Plugins.RabbitMQ/RabbitMqConsumer.cs:212](src/RayTree.Plugins.RabbitMQ/RabbitMqConsumer.cs#L212), [src/RayTree.Plugins.PostgreSQL/Outbox/PostgreSqlOutbox.cs:329](src/RayTree.Plugins.PostgreSQL/Outbox/PostgreSqlOutbox.cs#L329)
  Same class of issue already fixed for `RayTreeMeter.ChangeTag` — reflection-based parse per message/row.
  Fix: replace with a `switch`.

- [ ] **10. LZ4 compressor still double-buffers**
  [src/RayTree.Plugins.Compressors.Lz4/Lz4CompressorPlugin.cs:12-14,29-31](src/RayTree.Plugins.Compressors.Lz4/Lz4CompressorPlugin.cs#L12-L14)
  Unlike Gzip/Brotli (stream directly), LZ4 fully buffers `source` into a `MemoryStream` + `ToArray()` first. K4os supports a streaming `LZ4Stream` API. (The earlier fix addressed the oversized decompress buffer only, not this double-buffering.)
  Fix: use `LZ4Stream` to stream directly against the caller's streams.

- [ ] **11. MessagePack serializer uses `Typeless` mode**
  [src/RayTree.Plugins.Serializers.MessagePack/MessagePackSerializerPlugin.cs:17,25](src/RayTree.Plugins.Serializers.MessagePack/MessagePackSerializerPlugin.cs#L17)
  Resolves the runtime type via reflection on every (de)serialize call. A contractless resolver without Typeless would be faster since the type is already known from `EntityChange<TEntity>`.
  Fix: evaluate switching off `Typeless` given the generic `TEntity` is already statically known at the call site.

- [ ] **12. RabbitMQ default routing key allocates `ChangeType.ToString().ToLower()` per publish**
  [src/RayTree.Plugins.RabbitMQ/RabbitMqPublisherOptions.cs:69](src/RayTree.Plugins.RabbitMQ/RabbitMqPublisherOptions.cs#L69)
  Two allocations per publish; `ToLower()` is also culture-sensitive with no `StringComparison`/invariant overload.
  Fix: precomputed tag switch (same pattern as `RayTreeMeter.ChangeTag`), and use `ToLowerInvariant()`.

## Low / informational (not scheduled — clarify intent first)

- **`IRepository`/`PostgreSqlRepository` is configured and schema-migrates but is never invoked anywhere in `src/`**, and there's no public API path to call it (`EntityChangeTracker.Publisher` is `internal`). Either dead functionality or a missing public accessor — needs a product decision before any code change.
- `InMemoryDeduplicationStore.CleanupAsync` materializes an intermediate list before removal ([InMemoryDeduplicationStore.cs:21](src/RayTree.Core/Plugins/Deduplication/InMemoryDeduplicationStore.cs#L21)).
- `KafkaConsumer.GetHeaderBytes` does 5 linear scans over headers per message instead of one pass.
- `EntityChangeTracker.GetEntityId`: `PropertyInfo` is cached, but `GetValue` itself isn't compiled to a delegate — same class as `EntityColumnMapper` pre-fix, but cheaper (lookup already cached), so lower urgency.
