## Why

`InMemoryDeduplicationStore` is process-local and clears on restart, making it unsuitable for distributed deployments where multiple instances share a subscriber or where dedup state must survive process restarts. A Redis-backed implementation closes this gap.

## What Changes

- New `RayTree.Plugins.Deduplication.Redis` assembly implementing `IDeduplicationStore` using StackExchange.Redis.
- `RedisDeduplicationStore` stores correlation IDs in Redis with TTL-based expiry (no explicit cleanup pass needed — Redis handles it natively, but `CleanupAsync` is a no-op to satisfy the interface).
- Builder extension method `UseRedisDeduplication(IConnectionMultiplexer, ...)` on `IChangeSubscriberBuilder` / `IChangeTrackingBuilder` to wire the store.
- Unit tests in `RayTree.Plugins.Deduplication.Redis.Tests` (no Docker required — use `FakeItEasy` mocks or `StackExchange.Redis.Extensions` test doubles).
- Integration tests in the same test project using Testcontainers for Redis.

## Capabilities

### New Capabilities

- `redis-deduplication`: Redis-backed `IDeduplicationStore` that stores processed correlation IDs with TTL-based expiry; includes builder extension methods for wiring.

### Modified Capabilities

<!-- None — IDeduplicationStore contract is unchanged; no existing spec requirements change. -->

## Impact

- New project: `src/RayTree.Plugins.Deduplication.Redis/RayTree.Plugins.Deduplication.Redis.csproj`
- New test project: `tests/RayTree.Plugins.Deduplication.Redis.Tests/`
- Dependencies: `StackExchange.Redis` (added to `Directory.Packages.props`)
- No changes to existing assemblies or public APIs
- Testcontainers Redis image needed in test project
