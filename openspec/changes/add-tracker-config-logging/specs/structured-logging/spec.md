## ADDED Requirements

### Requirement: Builder configuration calls emit structured logs
`ChangeTrackingBuilder` SHALL emit a structured `Information` log for each top-level configuration call made by the caller: `UseOutbox`, `UsePublisher`, `UseSerializer`, `UseCompressor`, `UseRepository`, `UseDeduplicationStore`, `UseMeter`, `UsePublisherOptions`, and `UseSubscriberOptions`. Each log entry SHALL include the registered plugin's CLR type name (or the options class name for option-configurers) as a structured property so that the log stream documents how the tracker was wired up.

#### Scenario: Outbox registration is logged
- **WHEN** a caller invokes `builder.UseOutbox<MyOutbox>(factory)`
- **THEN** an `Information` log is emitted with `{Plugin}` equal to `"MyOutbox"` and a message identifying it as an outbox registration

#### Scenario: Each Use* method emits exactly one log entry
- **WHEN** a caller chains `UseOutbox`, `UsePublisher`, `UseSerializer`, `UseCompressor`, `UseRepository`, `UseDeduplicationStore`, and `UseMeter`
- **THEN** seven distinct `Information` log entries are captured, one per call, each with the corresponding plugin type name

#### Scenario: Null logger factory produces no configuration output
- **WHEN** a caller builds without supplying an `ILoggerFactory` (defaulting to `NullLoggerFactory.Instance`)
- **THEN** no configuration log entries are emitted and the builder still completes successfully

### Requirement: Per-entity configuration is logged
`ChangeTrackingBuilder.ForEntity<TEntity>` SHALL emit an `Information` log naming the configured entity type before invoking the configure delegate, and SHALL emit `Debug` logs for each per-entity plugin override (`UseOutbox`, `UsePublisher`, `UseSerializer`, `UseCompressor`, `UseRepository`, `UseConsumer`, `UseConsumerFactory`, `UseSubscriberOptions`, `OnInsert`/`OnUpdate`/`OnDelete`/`OnChange`) applied inside the delegate.

#### Scenario: ForEntity logs the entity type at Information
- **WHEN** a caller invokes `builder.ForEntity<Order>(b => { /* ... */ })`
- **THEN** an `Information` log is emitted with `{EntityType}` equal to `"Order"`

#### Scenario: Per-entity plugin overrides log at Debug
- **WHEN** the configure delegate calls `b.UseOutbox(...)` and `b.OnInsert("name", handler)`
- **THEN** a `Debug` log is emitted for each override with `{EntityType}` and the override kind (e.g. `{Override}` = `"Outbox"`, `"OnInsert:name"`) as structured properties

### Requirement: Tracker build emits a summary log
When `ChangeTrackingBuilder.Build()` or `BuildAsync()` completes the `BuildInternal` step, the builder SHALL emit a single `Information` log entry summarising the configured tracker. The log entry SHALL include the following structured properties: `{EntityTypes}` (the list of configured entity-type names), `{HasCustomMeter}` (bool), `{HasCustomDeduplicationStore}` (bool), `{HasCustomLoggerFactory}` (bool), and `{Plugins}` containing the registered global outbox, publisher, serializer, and compressor type names (or `"<none>"` when not registered).

#### Scenario: Build summary is emitted once
- **WHEN** `builder.Build()` is called
- **THEN** exactly one `Information` "tracker built" log entry is captured containing the listed structured properties

#### Scenario: Build summary reflects unconfigured plugins
- **WHEN** the builder is built without a global serializer registration
- **THEN** the build summary's `{Plugins}` property contains `Serializer = "<none>"`

### Requirement: Builder default-meter decision is logged
When `ChangeTrackingBuilder.BuildInternal` falls back to a default `RayTreeMeter` (because `UseMeter` was not called) it SHALL emit a `Debug` log indicating that a default meter was created and is owned by the tracker. (The `NullLoggerFactory` fallback path is already covered by the existing "Logger factory opt-in on the unified builder" requirement and is not restated here.)

#### Scenario: Default meter creation is logged
- **WHEN** `Build()` is called without a prior `UseMeter` call
- **THEN** a `Debug` log is emitted stating that a default `RayTreeMeter` was created and is owned by the tracker

### Requirement: Tracker initialization lifecycle is logged
`EntityChangeTracker.InitializeAsync` SHALL log at `Information` level when initialization begins and when it completes successfully, and SHALL log at `Debug` level after each major sub-step: publisher initialization complete, and consumer connections initialized. On failure, an `Error` log SHALL be emitted with the exception before re-throwing.

#### Scenario: Successful initialization emits start and complete logs
- **WHEN** `InitializeAsync` is called and completes without error
- **THEN** an `Information` "tracker initialization started" log is captured followed by an `Information` "tracker initialization completed" log
- **THEN** exactly one `Debug` "publisher initialized" log is captured with `{EntityTypeCount}` structured property, and exactly one `Debug` "consumers initialized" log is captured with `{ConsumerCount}` structured property

#### Scenario: Initialization failure is logged before re-throw
- **WHEN** `InitializeAsync` throws because a plugin's `InitializeAsync` fails
- **THEN** an `Error` log is emitted with the exception details and the failing sub-step before the exception propagates

### Requirement: ChangeTrackingHostedService logs DI startup details
When `ChangeTrackingHostedService.StartAsync` runs (guaranteed one-shot per host instance), it SHALL emit a single `Information` "ChangeTracking starting" log with `{ConfigurationBound}` (bool — captured at `AddChangeTracking` registration time and stored on the hosted service) indicating whether configuration was bound from `IConfiguration`. This is additive to the existing hosted-service lifecycle logs and consolidates DI-registration visibility with the host start event.

#### Scenario: Hosted service startup emits a registration-context log
- **WHEN** the host starts and `ChangeTrackingHostedService.StartAsync` is invoked
- **THEN** exactly one `Information` "ChangeTracking starting" log is emitted with `{ConfigurationBound}` matching whether `AddChangeTracking` received a non-null `IConfiguration`
