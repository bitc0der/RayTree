## 1. Builder Interface Refactor

- [x] 1.1 Add `ISharedHandlerBuilder<TEntity>` in `src/RayTree.Core/Tracking` with `OnInsert`, `OnUpdate`, `OnDelete`, and `OnChange` overloads taking only a handler delegate
- [x] 1.2 Add `IIsolatedHandlerBuilder<TEntity>` in `src/RayTree.Core/Tracking` with `OnInsert`, `OnUpdate`, `OnDelete`, and `OnChange` overloads taking a non-null `handlerName` plus the handler delegate
- [x] 1.3 Remove `OnInsert`, `OnUpdate`, `OnDelete`, and `OnChange` from `IEntityBuilder<TEntity>`
- [x] 1.4 Change the return type of `IEntityBuilder<TEntity>.UseConsumer(IQueueConsumer)` to `ISharedHandlerBuilder<TEntity>`
- [x] 1.5 Add `IEntityBuilder<TEntity>.UseConsumerFactory(Func<string, IQueueConsumer>)` returning `IIsolatedHandlerBuilder<TEntity>`
- [x] 1.6 Update XML doc comments on all three interfaces to describe mode selection, accumulation, and the handler-name stability contract

## 2. Builder Implementation

- [x] 2.1 Split `EntityBuilder<TEntity>` into a small entry class implementing `IEntityBuilder<TEntity>` and two post-fork classes: `SharedHandlerBuilder<TEntity>` and `IsolatedHandlerBuilder<TEntity>`
- [x] 2.2 `SharedHandlerBuilder<TEntity>` reuses the existing `EntitySubscriberBuilder<TEntity>` accumulation logic
- [x] 2.3 `IsolatedHandlerBuilder<TEntity>` accumulates `(handlerName, ChangeType?, ChangeHandlerAsync<TEntity>)` tuples; on `Apply()` validates that `(action, handlerName)` pairs are unique and the factory returns distinct non-null consumer instances per name
- [x] 2.4 Add validation that anonymous handler-name parameters (null or empty string) throw `ArgumentException` immediately at registration time

## 3. Subscriber Runtime — Isolated Path

- [x] 3.1 Add `Dictionary<(Type entityType, string handlerName), IQueueConsumer>` field to `ChangeSubscriber` for Isolated-mode consumer storage
- [x] 3.2 Add `Dictionary<(Type entityType, string handlerName), List<HandlerRegistration>>` field for Isolated-mode handlers, keyed by `(entity, handlerName)` instead of just entity
- [x] 3.3 Add `RegisterIsolatedHandler<TEntity>(string handlerName, ChangeType? changeType, ChangeHandlerAsync<TEntity>)` internal method on `ChangeSubscriber`
- [x] 3.4 Add `RegisterIsolatedConsumer<TEntity>(string handlerName, IQueueConsumer)` internal method
- [x] 3.5 Add `ProcessIsolatedMessageAsync(envelope, entityType, handlerName, ct)` private method mirroring `ProcessMessageAsync` but using dedup key `$"{correlationId}:{handlerName}"` and dispatching only to handlers registered under `handlerName` for the message's `ChangeType`
- [x] 3.6 Expose `IsolatedQueues` as a read-only dictionary on `ChangeSubscriber` for the hosted service

## 4. Hosted Service Integration

- [x] 4.1 Update `ChangeTrackingHostedService.StartAsync` to start one consume loop per entity for `Shared`-mode entities (existing behavior) AND one consume loop per `(entity, handlerName)` for `Isolated`-mode entities
- [x] 4.2 Each Isolated-mode loop calls `ChangeSubscriber.ConsumeIsolatedFromConsumerAsync(consumer, entityType, handlerName, ct)` — add this method to `ChangeSubscriber`
- [x] 4.3 Add `Information`-level logging for each started Isolated loop showing entity type and handler name; `Information` log on graceful shutdown

## 5. InMemoryQueue — Broadcast Variant

- [x] 5.1 Add `InMemoryBroadcastQueue` in `src/RayTree.Plugins.InMemory` implementing `IQueuePublisher` with internal `List<Channel<MessageEnvelope>>` of subscriber channels
- [x] 5.2 Add `Subscribe()` method on `InMemoryBroadcastQueue` returning a fresh `IQueueConsumer` that reads from a freshly added channel
- [x] 5.3 Publish writes to every subscribed channel; closed-channel handling for disposed subscribers
- [x] 5.4 Add unit tests covering fan-out delivery, late subscribers, and concurrent publish/subscribe

## 6. Shared-Mode Unit Tests

- [x] 6.1 Add test: two anonymous `OnInsert` handlers — both invoked in registration order
- [x] 6.2 Add test: anonymous `OnInsert` + catch-all `OnChange(null, ...)` — both invoked for an Insert message
- [x] 6.3 Add test: handlers for `OnInsert` and `OnUpdate` — Insert message invokes only the insert handler; Update only the update handler
- [x] 6.4 Add test: three handlers for the same action — invoked sequentially A → B → C
- [x] 6.5 Add test: first handler succeeds, second handler fails then succeeds on retry — invocation counts match (1 and 2)
- [x] 6.6 Add test: first handler succeeds, second exhausts retries with `SkipOnFailure = true` — second skipped, third continues
- [x] 6.7 Add test: first handler succeeds, second exhausts retries with `SkipOnFailure = false` — `RevertProcessedAsync` called with raw correlation ID, exception propagates
- [x] 6.8 Add test: handler exhausts retries with `SkipOnFailure = true` — `RevertProcessedAsync` NOT called

## 7. Isolated-Mode Unit Tests

- [x] 7.1 Add test: consumer factory invoked exactly once per unique handler name across registered handlers
- [x] 7.2 Add test: hosted service starts one loop per `(entity, handlerName)` pair
- [x] 7.3 Add test: each loop dispatches only its own named handler; cross-handler delivery isolation verified
- [x] 7.4 Add test: dedup key for Isolated mode is `$"{correlationId}:{handlerName}"` — verified by inspecting `TryMarkProcessedAsync` arguments
- [x] 7.5 Add test: `RevertProcessedAsync` on failed handler affects only its own dedup key
- [x] 7.6 Add test: handler A success and handler B failure are fully independent — A is not re-invoked when B fails
- [x] 7.7 Add test: per-handler retry budget — each handler gets its own `MaxRetries` attempts
- [x] 7.8 Add test: handler-name reordering in the registration code produces identical factory invocations, dedup keys, and consume loop bindings

## 8. Build-Time Validation Tests

- [x] 8.1 Add test: `IIsolatedHandlerBuilder.OnInsert("", handler)` throws `ArgumentException` immediately
- [x] 8.2 Add test: `IIsolatedHandlerBuilder.OnInsert(null, handler)` throws `ArgumentException` immediately
- [x] 8.3 Add test: duplicate `(action, handlerName)` registration causes `Build()` to throw `InvalidOperationException` whose message identifies the duplicate pair and entity
- [x] 8.4 Add test: consumer factory returning `null` for a name causes `Build()` to throw `InvalidOperationException` whose message contains the handler name
- [x] 8.5 Add test: consumer factory returning the same `IQueueConsumer` instance for two distinct names causes `Build()` to throw `InvalidOperationException`
- [x] 8.6 Compile-time check (compiled assertion, can be a Roslyn analyzer test or a doc-comment example): anonymous `OnInsert(handler)` is not reachable on `IIsolatedHandlerBuilder<TEntity>`; named `OnInsert(name, handler)` is not reachable on `ISharedHandlerBuilder<TEntity>`

## 9. Migration and Docs

- [x] 9.1 Update all internal `RayTree` callers (samples, integration test setups) to bind a consumer before registering handlers
- [x] 9.2 Add a "Handler dispatch modes" subsection to CLAUDE.md under the subscriber-side architecture overview explaining Shared vs Isolated, the consumer-binding fork, and dedup-key derivation
- [x] 9.3 Add a CHANGELOG entry under the next unreleased version noting the breaking removal of handler methods from `IEntityBuilder<TEntity>` and the new mode-selection API
- [x] 9.4 Document the known limitation that Shared-mode RabbitMQ / Kafka consumers ACK before processing — link to the follow-up `consumer-ack-after-handler` change once it exists

## 10. Verification

- [x] 10.1 `dotnet build RayTree.sln -c Release` succeeds with zero warnings
- [x] 10.2 All `RayTree.Core.Tests` pass, including the new Shared-mode and Isolated-mode test classes
- [x] 10.3 `RayTree.Plugins.InMemory.Tests` passes, including new broadcast-queue tests
- [ ] 10.4 Run integration test suites (`PostgreSQL`, `RabbitMQ`, `Kafka`) and confirm no regressions in Shared-mode flows
