## 1. Project & Package Setup

- [ ] 1.1 Add `StackExchange.Redis` and `Testcontainers.Redis` to `Directory.Packages.props`
- [ ] 1.2 Create `src/RayTree.Plugins.Deduplication.Redis/RayTree.Plugins.Deduplication.Redis.csproj` referencing `RayTree.Core` and `StackExchange.Redis`
- [ ] 1.3 Create `tests/RayTree.Plugins.Deduplication.Redis.Tests/RayTree.Plugins.Deduplication.Redis.Tests.csproj` referencing the plugin project, NUnit, Moq, Testcontainers, and `Testcontainers.Redis`
- [ ] 1.4 Add both projects to `RayTree.sln`

## 2. Core Implementation

- [ ] 2.1 Create `RedisDeduplicationOptions` class with `KeyPrefix` (default `"default"`), `RetentionPeriod` (default 24 h), and `Database` (default `-1`) properties
- [ ] 2.2 Create `RedisDeduplicationStore` implementing `IDeduplicationStore` — constructor takes `IConnectionMultiplexer` and `RedisDeduplicationOptions`
- [ ] 2.3 Implement `TryMarkProcessedAsync` using `StringSetAsync(key, "1", ttl, When.NotExists)` with key pattern `raytree:dedup:{KeyPrefix}:{correlationId}`
- [ ] 2.4 Implement `RevertProcessedAsync` using `KeyDeleteAsync(key)`
- [ ] 2.5 Implement `CleanupAsync` as a no-op (return `Task.CompletedTask`)
- [ ] 2.6 Resolve `IDatabase` from `IConnectionMultiplexer.GetDatabase(options.Database)` where `-1` calls the no-argument overload

## 3. Builder Extension Methods

- [ ] 3.1 Create static class `RedisDeduplicationExtensions` in the `RayTree` namespace
- [ ] 3.2 Add `UseRedisDeduplication(this IChangeSubscriberBuilder, IConnectionMultiplexer)` overload
- [ ] 3.3 Add `UseRedisDeduplication(this IChangeSubscriberBuilder, IConnectionMultiplexer, Action<RedisDeduplicationOptions>)` overload
- [ ] 3.4 Add `UseRedisDeduplication(this IChangeTrackingBuilder, IConnectionMultiplexer)` overload
- [ ] 3.5 Add `UseRedisDeduplication(this IChangeTrackingBuilder, IConnectionMultiplexer, Action<RedisDeduplicationOptions>)` overload

## 4. Unit Tests

- [ ] 4.1 Write test verifying `TryMarkProcessedAsync` calls `StringSetAsync` with `When.NotExists` and the correct TTL and key, returning `true` when mock returns `true`
- [ ] 4.2 Write test verifying `TryMarkProcessedAsync` returns `false` when mock `StringSetAsync` returns `false` (duplicate)
- [ ] 4.3 Write test verifying `RevertProcessedAsync` calls `KeyDeleteAsync` with the correctly-formatted key
- [ ] 4.4 Write test verifying `CleanupAsync` issues no Redis commands
- [ ] 4.5 Write test verifying key is formatted as `raytree:dedup:{prefix}:{correlationId}` with a custom prefix
- [ ] 4.6 Write test verifying default options produce `KeyPrefix = "default"`, `RetentionPeriod = 24h`, `Database = -1`
- [ ] 4.7 Write test verifying `Database = -1` calls `GetDatabase()` without arguments; non-negative value calls `GetDatabase(n)`

## 5. Integration Tests

- [ ] 5.1 Create `RedisDeduplicationIntegrationTests` using Testcontainers Redis fixture; spin up container once per test class
- [ ] 5.2 Write integration test: first `TryMarkProcessedAsync` call returns `true`, second returns `false`
- [ ] 5.3 Write integration test: mark → revert → mark again returns `true`
- [ ] 5.4 Write integration test: mark with 1-second TTL, wait for expiry, mark again returns `true`

## 6. Verification

- [ ] 6.1 Run `dotnet build RayTree.sln` (Debug) — zero warnings, zero errors
- [ ] 6.2 Run `dotnet test tests/RayTree.Plugins.Deduplication.Redis.Tests` — all tests pass (unit tests run without Docker; integration tests require Docker)
