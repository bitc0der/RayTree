## Context#

Currently, RayTree requires users to manually execute SQL scripts or use external migration tools to create storage schemas (source tables, outbox tables, notification triggers, DDL procedures). The framework also requires manual queue infrastructure setup (creating RabbitMQ exchanges/queues, Kafka topics). All initialization is left to the user.

The existing `EntityChangeTracker` already has `InitializeStorageAsync()` extension method that generates DDL, but it doesn't execute it. Queue infrastructure must be created separately.

Key existing components:
- `IDdlGenerator` / `CombinedDdlGenerator` - Generate CREATE/DROP SQL for tables and triggers
- `PostgreSqlDdlExecutor` - Execute DDL with statement splitting (but currently requires manual invocation)
- `IQueuePublisher` - Interface for queue publishers (RabbitMQ, Kafka, InMemory)
- `EntityChangeTracker` - Registers outboxes AND publishers per entity type
- `ChangeTrackingConfiguration` - Standalone builder pattern
- `EntityChangeInterceptor` - EF Core integration point#

## Goals / Non-Goals#

**Goals:**
- Automatically detect registered entity types and initialize their storage schemas when `EntityChangeTracker` is built/initialized
- Initialize queue infrastructure (RabbitMQ exchanges/queues, Kafka topics, etc.) at the same time
- Execute DDL via `PostgreSqlDdlExecutor` during tracker initialization
- Support both DI-based (via `Build()`) and standalone configurations
- Ensure idempotent initialization (CREATE IF NOT EXISTS patterns)#

**Non-Goals:**
- No automatic schema migrations or versioning (out of scope for initial release)
- No support for non-PostgreSQL storage in initial release
- No GUI or interactive management tools
- No explicit enable/disable API - auto-init is the default behavior#

## Decisions#

### 1. Automatic Detection via EntityChangeTracker#

**Decision**: Leverage `EntityChangeTracker.GetOutboxes()` and `GetPublishers()` to discover registered entity types and their configurations, then automatically generate and execute DDL AND queue setup during tracker initialization.#

**Rationale**: The tracker already maintains the mapping of entity types to outbox and publisher implementations. This is the single source of truth for what needs initialization.#

**Alternatives considered**:
- Require explicit registration via builder API - more verbose, easy to forget
- Scan assemblies for entity types - too magical, may pick up wrong types#

### 2. InitializeStorageAsync() and InitializeQueues() During Build#

**Decision**: Modify `Build()` and `BuildAsync()` methods to automatically call storage and queue initialization.#

**Rationale**: Users expect the tracker to be ready to use after building. Requiring separate initialization calls is a surprising behavior. Auto-init on build is the simplest developer experience.#

**Implementation**:
```csharp
// In EntityChangeTracker.Build() or equivalent
public static async Task<EntityChangeTracker> BuildAsync(this EntityChangeTracker tracker, CancellationToken ct = default)
{
    await tracker.InitializeStorageAsync(ct);
    await tracker.InitializeQueuesAsync(ct);
    return tracker;
}
```

**Alternatives considered**:
- Separate initialization call after build - more complex API, easy to forget
- Lazy initialization on first use - complex, may cause race conditions#

### 3. Queue Initialization Interface#

**Decision**: Add `InitializeAsync()` method to `IQueuePublisher` interface for queue infrastructure setup.#

**Rationale**: Each queue provider knows what infrastructure it needs (RabbitMQ: exchanges/queues, Kafka: topics). The interface should support async initialization.#

**Implementation**:
```csharp
public interface IQueuePublisher
{
    Task InitializeAsync(CancellationToken ct = default);
    Task PublishAsync(EntityChange change, PipeReader payload, CancellationToken ct = default);
}
```

**Alternatives considered**:
- Separate `IQueueInitializer` interface - more complex, splits related concerns
- Configuration-based setup - less flexible, harder to customize per provider#

### 4. Integration via Build() Methods#

**Decision**: Integrate storage and queue initialization into the existing `Build()` and `BuildAsync()` methods in both DI and standalone configurations.#

**Rationale**: The build step is when the tracker is fully configured. This is the natural point to ensure everything is ready.#

**DI Integration**:
```csharp
// In AddChangeTracking() - after building the tracker
var tracker = builder.Build(); // Build() now auto-initializes storage + queues
```

**Standalone Integration**:
```csharp
var config = new ChangeTrackingConfiguration()
    .UsePostgreSqlOutbox(options => ...)
    .UseRabbitMqPublisher(options => ...);

var tracker = await config.BuildAsync(); // BuildAsync() initializes storage + queues automatically
```

**Alternatives considered**:
- Separate hosted service for initialization - adds overhead, init is fast
- Explicit initialization call required - more API surface, easy to forget#

### 5. Idempotent DDL with CREATE IF NOT EXISTS#

**Decision**: Enhance `PostgreSqlDdlExecutor` and DDL generators to use `CREATE TABLE IF NOT EXISTS`, `CREATE FUNCTION IF NOT EXISTS`, and `CREATE TRIGGER IF NOT EXISTS` patterns.#

**Rationale**: Initialization should be safe to call multiple times (idempotent). This enables calling it on every build without fear of errors.#

**Implementation**:
- Modify `SourceTableDdlGenerator`, `OutboxTableDdlGenerator`, `TriggerDdlGenerator` to output `IF NOT EXISTS` variants
- `PostgreSqlDdlExecutor` already handles statement splitting - just ensure it works with new syntax#

**Alternatives considered**:
- Check existence before CREATE - more complex, race condition possible
- Drop and recreate - destructive, loses data#

## Risks / Trade-offs#

| Risk | Mitigation |
|------|-----------|
| DDL execution requires elevated storage privileges | Document required privileges; provide clear error messages when permission denied |
| Queue initialization requires broker permissions | Document required permissions for each queue provider |
| Initialization slows startup | Make it async and non-blocking where possible |
| Idempotent DDL may mask real errors | Log all DDL operations at DEBUG level for troubleshooting |
| Connection string discovery from outbox config | Add `IOutbox.GetConnectionString()` to interface |
| Queue-specific configuration discovery | Store queue config in `IQueuePublisher` implementation |

## Migration Plan#

1. Add `InitializeStorageAsync()` and `InitializeQueuesAsync()` logic to `EntityChangeTracker` extensions
2. Enhance DDL generators with `IF NOT EXISTS` patterns
3. Add `InitializeAsync()` to `IQueuePublisher` and implement in all providers
4. Integrate auto-init into `Build()` and `BuildAsync()` methods
5. Test with existing integration test infrastructure (Testcontainers)#

## Open Questions#

- Should initialization verify that tables/queues match expected schema or just check existence?
- Should we support schema *updates* (ALTER TABLE) in future versions?
- How to handle connection string discovery when multiple outboxes use different databases?
- Should queue initialization be optional (some teams may create infrastructure separately)?
