## ADDED Requirements

### Requirement: Redis-backed deduplication store
The system SHALL provide a `RedisDeduplicationStore` class implementing `IDeduplicationStore` that stores processed correlation IDs in Redis using TTL-based expiry. The store SHALL use atomic `SET NX EX` for marking, `DEL` for reverting, and SHALL treat `CleanupAsync` as a no-op (TTL handles expiry natively).

#### Scenario: First-time correlation ID is marked as processed
- **WHEN** `TryMarkProcessedAsync` is called with a correlation ID not present in Redis
- **THEN** the key is written to Redis with a configurable TTL and the method returns `true`

#### Scenario: Duplicate correlation ID is detected
- **WHEN** `TryMarkProcessedAsync` is called with a correlation ID that already exists in Redis
- **THEN** no key is written and the method returns `false`

#### Scenario: Correlation ID is reverted after handler failure
- **WHEN** `RevertProcessedAsync` is called with a previously marked correlation ID
- **THEN** the Redis key is deleted so the message can be reprocessed

#### Scenario: Cleanup is a no-op
- **WHEN** `CleanupAsync` is called with any retention period
- **THEN** no Redis commands are issued and the method completes successfully

### Requirement: Configurable key prefix and TTL
The system SHALL provide `RedisDeduplicationOptions` with a `KeyPrefix` property (default `"default"`) and a `RetentionPeriod` property (default 24 hours). Keys SHALL be stored under the pattern `raytree:dedup:{KeyPrefix}:{correlationId}`.

#### Scenario: Keys are namespaced by prefix
- **WHEN** `RedisDeduplicationOptions.KeyPrefix` is set to `"orders"`
- **THEN** correlation ID `"abc"` is stored under the Redis key `raytree:dedup:orders:abc`

#### Scenario: Default key prefix is applied when not configured
- **WHEN** `RedisDeduplicationOptions` is constructed with defaults
- **THEN** `KeyPrefix` equals `"default"` and `RetentionPeriod` equals 24 hours

#### Scenario: TTL is applied at write time
- **WHEN** `TryMarkProcessedAsync` successfully marks a correlation ID
- **THEN** the Redis key has a TTL equal to `RetentionPeriod` rounded to the nearest second

### Requirement: Configurable Redis database selection
The system SHALL support selecting a Redis logical database via `RedisDeduplicationOptions.Database` (default `-1`, which selects the default database from the connection multiplexer).

#### Scenario: Custom database is selected
- **WHEN** `RedisDeduplicationOptions.Database` is set to `2`
- **THEN** all Redis operations target logical database `2`

#### Scenario: Default database is used when not configured
- **WHEN** `RedisDeduplicationOptions.Database` is `-1`
- **THEN** `IConnectionMultiplexer.GetDatabase()` is called without arguments, using the default database

### Requirement: Builder extension methods for wiring Redis deduplication
The system SHALL provide extension methods `UseRedisDeduplication(IConnectionMultiplexer)` and `UseRedisDeduplication(IConnectionMultiplexer, Action<RedisDeduplicationOptions>)` on both `IChangeSubscriberBuilder` and `IChangeTrackingBuilder`. These SHALL register a `RedisDeduplicationStore` as the active deduplication store.

#### Scenario: Wiring via IChangeSubscriberBuilder with defaults
- **WHEN** `builder.UseRedisDeduplication(multiplexer)` is called on an `IChangeSubscriberBuilder`
- **THEN** the resulting `ChangeSubscriber` uses `RedisDeduplicationStore` with default options

#### Scenario: Wiring via IChangeTrackingBuilder with custom options
- **WHEN** `builder.UseRedisDeduplication(multiplexer, o => o.KeyPrefix = "payments")` is called on an `IChangeTrackingBuilder`
- **THEN** the `EntityChangeTracker`'s subscriber uses a `RedisDeduplicationStore` scoped to the `payments` key prefix

### Requirement: Unit tests without Docker
The system SHALL include unit tests for `RedisDeduplicationStore` that mock `IConnectionMultiplexer` and `IDatabase` to verify correct Redis command usage, key formatting, and return-value mapping — without requiring a running Redis instance.

#### Scenario: TryMarkProcessedAsync issues SET NX EX command
- **WHEN** `TryMarkProcessedAsync` is called in a unit test with a mocked IDatabase
- **THEN** `StringSetAsync` is called with `When.NotExists` and the expected TTL, and the result matches the mock's return value

#### Scenario: RevertProcessedAsync issues DEL command
- **WHEN** `RevertProcessedAsync` is called in a unit test with a mocked IDatabase
- **THEN** `KeyDeleteAsync` is called with the correctly-formatted key

### Requirement: Integration tests using Testcontainers Redis
The system SHALL include integration tests that spin up a real Redis container via Testcontainers, verify end-to-end dedup behaviour including TTL expiry, and exercise `RevertProcessedAsync` against a live Redis instance.

#### Scenario: Deduplication prevents double-processing against real Redis
- **WHEN** `TryMarkProcessedAsync` is called twice with the same correlation ID against a live Redis container
- **THEN** the first call returns `true` and the second returns `false`

#### Scenario: Revert allows reprocessing against real Redis
- **WHEN** `TryMarkProcessedAsync` marks a key, then `RevertProcessedAsync` removes it, then `TryMarkProcessedAsync` is called again
- **THEN** the final call returns `true` (key was absent after revert)

#### Scenario: Keys expire after TTL
- **WHEN** a correlation ID is marked with a very short TTL and the TTL elapses
- **THEN** `TryMarkProcessedAsync` with the same ID returns `true` (key expired and is no longer present)
