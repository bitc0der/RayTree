## Context

`RayTree.Core` ships `InMemoryDeduplicationStore` as the default `IDeduplicationStore`. It uses a `ConcurrentDictionary<string, DateTime>` which is process-local and clears on restart. For distributed deployments — multiple publisher/subscriber instances sharing message queues — duplicate detection must survive process restarts and be visible across all nodes. Redis is the natural fit: it is already common infrastructure in distributed .NET systems and StackExchange.Redis is the de facto client.

The `IDeduplicationStore` interface has three methods: `TryMarkProcessedAsync` (atomic add, return false on duplicate), `RevertProcessedAsync` (remove on handler failure), and `CleanupAsync` (evict old entries). Redis TTL-based expiry handles cleanup natively, making `CleanupAsync` a no-op.

## Goals / Non-Goals

**Goals:**
- Implement `IDeduplicationStore` backed by Redis, with atomic SET NX EX semantics for `TryMarkProcessedAsync`.
- Provide `RevertProcessedAsync` using Redis `DEL`.
- Make `CleanupAsync` a no-op (TTL on each key handles expiry; no scan needed).
- Ship as a separate assembly `RayTree.Plugins.Deduplication.Redis` following the existing plugin assembly naming pattern.
- Provide a builder extension method `UseRedisDeduplication(IConnectionMultiplexer, options)` on both `IChangeSubscriberBuilder` and `IChangeTrackingBuilder`.
- Ship unit tests (mocked Redis) and integration tests (Testcontainers Redis) in `tests/RayTree.Plugins.Deduplication.Redis.Tests`.

**Non-Goals:**
- Redis Cluster / Sentinel failover configuration — StackExchange.Redis handles this at the connection level; the plugin is oblivious.
- Lua scripting for multi-key atomicity — the three operations are each single-key and are natively atomic.
- Sliding TTL (resetting on access) — a fixed TTL matching `DeduplicationRetention` is sufficient.
- Custom serialization of correlation IDs — they are already strings.

## Decisions

### Key naming

**Decision**: Keys are stored as `raytree:dedup:{keyPrefix}:{correlationId}` where `keyPrefix` defaults to `"default"` and is configurable via `RedisDeduplicationOptions.KeyPrefix`. This allows multiple `RayTree` deployments sharing one Redis instance to avoid collisions.

**Alternatives considered**: Using a Redis Hash per prefix — rejected because it would require atomic `HSETNX` + expire logic and complicate `RevertProcessedAsync`.

### TTL strategy

**Decision**: Each key is written with `SET key 1 NX EX ttlSeconds` where `ttlSeconds = options.RetentionPeriod.TotalSeconds` (default 24 h, mirroring `SubscriberOptions.DeduplicationRetention`). TTL is set at write time; `CleanupAsync` is a no-op.

**Alternatives considered**: No TTL + periodic `SCAN` cleanup in `CleanupAsync` — rejected because SCAN is O(keyspace) and adds operational burden. StackExchange.Redis's `KeyExpireAsync` after `StringSetAsync` — rejected because two round-trips break atomicity.

### Atomicity for `TryMarkProcessedAsync`

**Decision**: Single `StringSetAsync(key, "1", ttl, When.NotExists)` call. Redis `SET NX EX` is atomic at the server level — no Lua script needed.

### Database selection

**Decision**: `RedisDeduplicationOptions.Database` (default `-1` = default DB) is passed to `IConnectionMultiplexer.GetDatabase(db)`. This mirrors how StackExchange.Redis callers typically select the database.

### Extension method placement

**Decision**: Extension methods live in `RayTree.Plugins.Deduplication.Redis` assembly in a `RayTree` namespace for ergonomic `using`. Two overloads: `UseRedisDeduplication(IConnectionMultiplexer)` and `UseRedisDeduplication(IConnectionMultiplexer, Action<RedisDeduplicationOptions>)`. These extend both `IChangeSubscriberBuilder` and `IChangeTrackingBuilder`.

## Risks / Trade-offs

- **Redis unavailability** → `TryMarkProcessedAsync` will throw; the caller (`ChangeSubscriber`) will propagate the exception and the message will be NACKed/retried by the broker. This is correct at-least-once behaviour but means Redis downtime blocks message processing. Mitigation: operators should use Redis Sentinel or Cluster for HA; this is out of scope for the plugin.
- **TTL shorter than `DeduplicationRetention`** → if `RetentionPeriod` is configured shorter than the broker's message retention, a redelivered message could slip through as a non-duplicate. Documentation should warn that `RetentionPeriod` must be at least as long as the maximum broker redelivery window.
- **Key size** → correlation IDs are UUIDs (36 chars) + prefix overhead (~20 chars) = ~60 bytes per key. At millions of messages per day this is negligible.

## Migration Plan

- No migration required for existing deployments — `InMemoryDeduplicationStore` remains the default. Callers opt in by calling `UseRedisDeduplication(...)` in their builder chain.
- Rolling upgrade: switching from in-memory to Redis dedup is safe — the brief window of dual-process deployment may see a small set of messages processed twice (the in-memory process doesn't share state with the Redis-backed one). This is acceptable for most deployments; callers needing strict at-most-once during rollout should use a maintenance window.
