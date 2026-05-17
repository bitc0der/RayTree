## ADDED Requirements

### Requirement: Builder forks into mode-specific interface on consumer binding
`IEntityBuilder<TEntity>` SHALL expose two consumer-binding methods. Each returns a distinct post-fork builder interface that exposes only the handler-registration API appropriate to its dispatch mode.

#### Scenario: `UseConsumer` returns the shared-mode builder
- **WHEN** `UseConsumer(IQueueConsumer)` is called on `IEntityBuilder<TEntity>`
- **THEN** the method SHALL return an `ISharedHandlerBuilder<TEntity>` whose `OnInsert`, `OnUpdate`, `OnDelete`, and `OnChange` overloads take only a handler delegate (no name parameter)

#### Scenario: `UseConsumerFactory` returns the isolated-mode builder
- **WHEN** `UseConsumerFactory(Func<string, IQueueConsumer>)` is called on `IEntityBuilder<TEntity>`
- **THEN** the method SHALL return an `IIsolatedHandlerBuilder<TEntity>` whose `OnInsert`, `OnUpdate`, `OnDelete`, and `OnChange` overloads require a non-null, non-empty `handlerName` as the first parameter

#### Scenario: Handler-registration methods removed from the entry builder
- **WHEN** the caller attempts to call `OnInsert`, `OnUpdate`, `OnDelete`, or `OnChange` directly on `IEntityBuilder<TEntity>` without first binding a consumer
- **THEN** the call SHALL fail to compile because the methods do not exist on `IEntityBuilder<TEntity>`

### Requirement: Shared mode — handlers accumulate, run sequentially, share one delivery
Under Shared mode, every call to `OnInsert`, `OnUpdate`, `OnDelete`, or `OnChange` on `ISharedHandlerBuilder<TEntity>` SHALL add a new handler to the entity's handler list. Handlers SHALL execute in registration order, sequentially, on a single in-process delivery of each message.

#### Scenario: Two anonymous OnInsert handlers both invoked
- **WHEN** `.OnInsert(handlerA)` and then `.OnInsert(handlerB)` are registered on `ISharedHandlerBuilder<Order>`
- **THEN** an Insert message for `Order` SHALL invoke `handlerA` and then `handlerB`, in that order, on the same delivery

#### Scenario: Mixed action handlers preserve isolation between actions
- **WHEN** `.OnInsert(handlerA)` and `.OnUpdate(handlerB)` are registered
- **THEN** an Insert message SHALL invoke only `handlerA`; an Update message SHALL invoke only `handlerB`

#### Scenario: Sequential invocation order preserved across three handlers
- **WHEN** `.OnInsert(handlerA)`, `.OnInsert(handlerB)`, and `.OnChange(null, handlerC)` are registered on the same entity
- **THEN** an Insert message SHALL invoke `handlerA`, wait for it to complete, invoke `handlerB`, wait for it to complete, then invoke `handlerC`

### Requirement: Shared mode — per-handler retry, message-scoped dedup revert
Under Shared mode, each handler SHALL be retried independently up to `SubscriberOptions.MaxRetries`. A handler exhausting retries with `SkipOnFailure = false` SHALL cause the message-level dedup mark (keyed by `correlationId`) to be reverted before the exception propagates, so any redelivered message will re-invoke every handler including those that previously succeeded.

#### Scenario: Second handler retried independently of the first
- **WHEN** `handlerA` succeeds on the first attempt and `handlerB` fails once then succeeds on the second attempt
- **THEN** `handlerA` is invoked exactly once and `handlerB` is invoked exactly twice within the same message processing

#### Scenario: SkipOnFailure isolates a failed handler from later ones
- **WHEN** `handlerA` exhausts all retries and `SubscriberOptions.SkipOnFailure = true`
- **THEN** the failure is logged at `Error`, `handlerA` returns control without throwing, and subsequent handlers continue executing

#### Scenario: Dedup revert when a non-skipped handler fails
- **WHEN** `handlerA` succeeds and `handlerB` then exhausts all retries with `SkipOnFailure = false`
- **THEN** `IDeduplicationStore.RevertProcessedAsync` SHALL be called with the message's correlation ID, and the exception SHALL propagate out of `ProcessMessageAsync`

#### Scenario: No dedup revert when failure is skipped
- **WHEN** `handlerB` exhausts retries with `SkipOnFailure = true`
- **THEN** `RevertProcessedAsync` SHALL NOT be called and the message is considered fully processed

### Requirement: Isolated mode — one consume loop per `(entity, handlerName)`
Under Isolated mode, the consumer factory `Func<string, IQueueConsumer>` SHALL be invoked exactly once per distinct handler name registered for the entity. The framework SHALL start one consume loop per `(entity type, handler name)` pair when the host starts.

#### Scenario: Factory invoked once per unique handler name
- **WHEN** the user registers `.OnInsert("read-model", h1)`, `.OnUpdate("read-model", h2)`, and `.OnInsert("notifier", h3)` on `IIsolatedHandlerBuilder<Order>`
- **THEN** the factory SHALL be invoked exactly twice — once with `"read-model"` and once with `"notifier"`

#### Scenario: One consume loop per (entity, handlerName)
- **WHEN** an entity has two named handlers `"read-model"` and `"notifier"` in Isolated mode
- **THEN** `ChangeTrackingHostedService.StartAsync` SHALL start two consume loops for that entity — one for `"read-model"` and one for `"notifier"` — each consuming from its own `IQueueConsumer`

#### Scenario: Each loop dispatches only its own handler
- **WHEN** a message reaches the `"read-model"` consume loop and the `"notifier"` consume loop independently
- **THEN** the `"read-model"` loop SHALL invoke only handlers registered under name `"read-model"` for the message's `ChangeType`; the `"notifier"` loop SHALL invoke only handlers registered under name `"notifier"`

### Requirement: Isolated mode — per-handler dedup key
Under Isolated mode, the deduplication key SHALL be `$"{correlationId}:{handlerName}"`. A failed and redelivered message under handler `"notifier"` MUST NOT cause handler `"read-model"` to re-execute, and vice versa.

#### Scenario: Independent dedup namespaces per handler name
- **WHEN** a message with correlation ID `C` is delivered to both the `"read-model"` and `"notifier"` consume loops
- **THEN** `TryMarkProcessedAsync` SHALL be called once with `"C:read-model"` and once with `"C:notifier"` — neither call blocks the other

#### Scenario: Revert affects only the failing handler
- **WHEN** `handlerB` named `"notifier"` exhausts retries with `SkipOnFailure = false`
- **THEN** `RevertProcessedAsync` SHALL be called with `"{correlationId}:notifier"` only; the `"read-model"` dedup entry remains intact

### Requirement: Isolated mode — per-handler retry, independent failure isolation
Each named handler SHALL have its own retry budget. A handler's failure cannot consume retries from, nor force re-execution of, any other handler.

#### Scenario: Handler A succeeds while handler B exhausts retries
- **WHEN** `"read-model"` succeeds on its delivery and `"notifier"` exhausts all retries with `SkipOnFailure = false` on its independent delivery of the same message
- **THEN** `"read-model"` SHALL NOT be re-invoked as a result of `"notifier"`'s failure

#### Scenario: SubscriberOptions apply per consume loop
- **WHEN** `SubscriberOptions.MaxRetries = 3` is configured for the entity
- **THEN** each named handler's consume loop SHALL apply that retry budget independently

### Requirement: Isolated mode — build-time validation of handler names
Handler names registered through `IIsolatedHandlerBuilder<TEntity>` MUST be non-null, non-empty strings. The pair `(action, handlerName)` MUST be unique within an entity. The consumer factory MUST return a distinct `IQueueConsumer` instance for each handler name.

#### Scenario: Null or empty handler name rejected at registration
- **WHEN** the caller invokes `.OnInsert("", handler)` or `.OnInsert(null, handler)` on `IIsolatedHandlerBuilder<Order>`
- **THEN** the method SHALL throw `ArgumentException` immediately

#### Scenario: Duplicate (action, handlerName) rejected at build time
- **WHEN** the caller registers `.OnInsert("read-model", h1)` and `.OnInsert("read-model", h2)` on the same entity
- **THEN** `Build()` SHALL throw `InvalidOperationException` identifying the entity type and duplicate `(Insert, "read-model")` pair

#### Scenario: Factory returning null rejected at build time
- **WHEN** `UseConsumerFactory` returns `null` for some handler name `"x"`
- **THEN** `Build()` SHALL throw `InvalidOperationException` whose message contains the handler name `"x"`

#### Scenario: Factory returning the same instance for different names rejected
- **WHEN** the factory returns the same `IQueueConsumer` instance for handler names `"a"` and `"b"`
- **THEN** `Build()` SHALL throw `InvalidOperationException` explaining that each handler name requires an independent consumer instance

### Requirement: Handler names are stable identifiers, not opaque indices
Handler names registered in Isolated mode form part of the deployed configuration and the broker subscription topology. Renaming a handler is equivalent to creating a new subscription. Reordering handler registrations MUST NOT affect handler identity, dedup keys, or consume-loop bindings.

#### Scenario: Reordering registrations does not affect identity
- **WHEN** the order of `.OnInsert("read-model", h1)` and `.OnInsert("notifier", h2)` is swapped in the registration code
- **THEN** the consume loop, dedup keys, and factory invocations for `"read-model"` and `"notifier"` remain bit-for-bit identical

### Requirement: Existing Shared dispatch path is preserved
Callers that bind a consumer via `UseConsumer(IQueueConsumer)` and register anonymous handlers SHALL observe behavior bit-for-bit equivalent to the current single-consumer-per-entity dispatch model. Removing handler methods from `IEntityBuilder<TEntity>` is the only source-level change required of such callers.

#### Scenario: Shared-mode wiring matches pre-change runtime
- **WHEN** a Shared-mode entity has any number of anonymous handlers registered through `ISharedHandlerBuilder<TEntity>`
- **THEN** the resulting `ChangeSubscriber` SHALL register the entity with one queue, dispatch every matching handler sequentially per message, and use `correlationId` as the dedup key

#### Scenario: Mode flip requires only an explicit API change
- **WHEN** a caller migrates an entity from Shared to Isolated by changing `UseConsumer(...)` to `UseConsumerFactory(...)` and adding names to handler registrations
- **THEN** no other code change is required; publisher-side wiring is unaffected
