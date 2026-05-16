## ADDED Requirements

### Requirement: Handler registration accumulates — does not replace
Each call to `OnInsert`, `OnUpdate`, `OnDelete`, or `OnChange` on `IEntitySubscriberBuilder<TEntity>` or `IEntityBuilder<TEntity>` SHALL add a new handler to the entity's handler list. A second call for the same action type SHALL NOT remove or replace any previously registered handler.

#### Scenario: Two OnInsert handlers both invoked
- **WHEN** `OnInsert(handlerA)` and then `OnInsert(handlerB)` are registered for the same entity type
- **THEN** both `handlerA` and `handlerB` SHALL be invoked (in registration order) for every Insert message for that entity

#### Scenario: Three handlers of mixed action types all invoked
- **WHEN** `OnInsert(handlerA)`, `OnInsert(handlerB)`, and `OnChange(null, handlerC)` (catch-all) are registered for the same entity type
- **THEN** an Insert message SHALL invoke `handlerA`, then `handlerB`, then `handlerC` in that order

#### Scenario: Handlers for different action types are independent
- **WHEN** `OnInsert(handlerA)` and `OnUpdate(handlerB)` are registered for the same entity type
- **THEN** an Insert message SHALL invoke only `handlerA`; an Update message SHALL invoke only `handlerB`

### Requirement: Handlers invoked sequentially in registration order
Multiple handlers for the same entity + action type SHALL be invoked one at a time, in the order they were registered. The next handler SHALL NOT start until the previous one has completed (or failed all retries).

#### Scenario: Sequential invocation order preserved
- **WHEN** three handlers are registered in order A, B, C for the same entity + action type
- **THEN** A completes before B starts, and B completes before C starts

### Requirement: Retry semantics apply per handler independently
Each handler is retried independently according to `SubscriberOptions.MaxRetries`. A handler failing does not consume retry attempts for subsequent handlers.

#### Scenario: Second handler retried independently of first
- **WHEN** `handlerA` succeeds on the first attempt and `handlerB` fails on the first attempt but succeeds on the second
- **THEN** `handlerA` is invoked once and `handlerB` is retried exactly once, totalling two invocations for `handlerB`

#### Scenario: SkipOnFailure isolates a failed handler
- **WHEN** `handlerA` fails all retries and `SubscriberOptions.SkipOnFailure = true`
- **THEN** `handlerA` is skipped (error logged) and subsequent handlers in the list continue executing normally

### Requirement: Message-level dedup revert when any non-skip handler fails
If any handler exhausts its retries and `SkipOnFailure = false`, the message's dedup mark SHALL be reverted so the redelivered message can be retried. All handlers — including those that already succeeded — will be invoked again on redelivery (at-least-once delivery is message-scoped, not handler-scoped). Handlers MUST be idempotent.

#### Scenario: Dedup mark reverted when second handler fails
- **WHEN** `handlerA` succeeds and `handlerB` then exhausts all retries with `SkipOnFailure = false`
- **THEN** the dedup mark for the message correlation ID SHALL be reverted, causing the redelivered message to re-invoke both `handlerA` and `handlerB`

#### Scenario: No dedup revert when all failures are skipped
- **WHEN** `handlerB` exhausts all retries but `SkipOnFailure = true`
- **THEN** the dedup mark SHALL NOT be reverted; the message is considered fully processed and will not be redelivered
