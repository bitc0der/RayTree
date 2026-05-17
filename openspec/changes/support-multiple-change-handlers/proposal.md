## Why

Today the fluent builder lets callers register multiple handlers per entity + action (the dispatch loop already iterates all of them), but the behavior is undocumented, untested, and exposes a real reliability problem: handlers share a single broker delivery, so one handler's failure can affect the others through dedup-revert, and the existing RabbitMQ / Kafka consumers ACK before the subscriber sees the message — meaning the at-least-once retry story doesn't actually fire through the broker.

Real applications need two distinct patterns: (1) lightweight in-process composition where several handlers do related work for a single message, and (2) independent downstream subscribers (read-model, notifier, audit pipeline) that must fail and retry without cross-contamination. The current API conflates both into one mode and doesn't deliver on the second.

## What Changes

- Introduce two handler-dispatch strategies, chosen at consumer-binding time on `IEntityBuilder<TEntity>`:
  - **`UseConsumer(IQueueConsumer)`** → returns `ISharedHandlerBuilder<TEntity>` (one broker delivery, all handlers run sequentially in-process; today's behaviour, now explicit and accumulating).
  - **`UseConsumerFactory(Func<string, IQueueConsumer>)`** → returns `IIsolatedHandlerBuilder<TEntity>` (one broker subscription per named handler; independent delivery, retry, and dedup per handler).
- Add `ISharedHandlerBuilder<TEntity>` with anonymous handler overloads: `OnInsert(handler)`, `OnUpdate(handler)`, `OnDelete(handler)`, `OnChange(changeType, handler)`. Each call accumulates a handler; second call does not replace the first.
- Add `IIsolatedHandlerBuilder<TEntity>` with mandatory-name overloads: `OnInsert(string handlerName, handler)`, `OnUpdate(...)`, `OnDelete(...)`, `OnChange(string handlerName, changeType, handler)`. Names must be unique per entity and stable across deployments.
- Remove `OnInsert` / `OnUpdate` / `OnDelete` / `OnChange` from `IEntityBuilder<TEntity>`. Handler registration is reachable only via the post-fork builders. **BREAKING** for callers that register handlers before binding a consumer — fix is to reorder the lines.
- `ChangeSubscriber` gains an Isolated dispatch path: one consume loop per `(entity, handlerName)`, dedup key is `correlationId + handlerName`, and each handler has its own retry budget.
- Plugin contract: `IQueueConsumer` itself is unchanged. Plugins opt into Isolated mode by being constructable from the factory `Func<string, IQueueConsumer>` (the user maps the handler name to broker identity — Kafka `GroupId`, RabbitMQ queue, etc.). InMemoryQueue ships a broadcast variant for Isolated use.

## Capabilities

### New Capabilities

- `multiple-change-handlers`: Two handler-dispatch modes (`Shared` and `Isolated`) selected by consumer-binding method, with accumulating handler registration, mode-specific dedup semantics, and compile-time enforcement of the mode/name contract through three builder interfaces.

### Modified Capabilities

- `subscriber-configuration`: The fluent example becomes mode-aware (shows both `UseConsumer` + anonymous handlers and `UseConsumerFactory` + named handlers). Handler registration moves from `IEntityBuilder<TEntity>` to the post-fork builders.

## Impact

- **`IEntityBuilder<TEntity>`** (`src/RayTree.Core/Tracking`) — `OnInsert/OnUpdate/OnDelete/OnChange` are removed; `UseConsumer` return type changes to `ISharedHandlerBuilder<TEntity>`; new `UseConsumerFactory` method.
- **New interfaces** — `ISharedHandlerBuilder<TEntity>` and `IIsolatedHandlerBuilder<TEntity>` in `RayTree.Core.Tracking`.
- **`EntityBuilder<TEntity>`** — splits into a small entry class plus two post-fork classes implementing the new interfaces. Existing `EntitySubscriberBuilder<TEntity>` is repurposed for the Shared path; a new `IsolatedEntitySubscriberBuilder<TEntity>` carries the named-handler list and its consumer factory.
- **`ChangeSubscriber`** — gains an `IsolatedHandlers` collection keyed by `(Type, string)` and an Isolated dispatch loop in `ChangeTrackingHostedService`. Existing `Shared`-mode dispatch path remains.
- **`IDeduplicationStore`** — usage extended: Isolated mode dedup keys are `"{correlationId}:{handlerName}"`. The store interface itself is unchanged (still string keys); only the key derivation in the subscriber changes.
- **`ChangeTrackingHostedService`** — starts one consume loop per entity in Shared mode (today's behavior) and one per `(entity, handlerName)` pair in Isolated mode.
- **Plugins** — InMemoryQueue gains a broadcast mode. RabbitMQ / Kafka consumers continue to work as-is for Shared; for Isolated, the user-supplied factory closes over distinct topology (queue name / `GroupId`) per handler name.
- **Tests** — `RayTree.Core.Tests` adds a Shared-mode multi-handler test class and a new Isolated-mode test class. InMemory broadcast queue gets its own test class.
- **Docs** — CLAUDE.md gets a new "Handler dispatch modes" subsection under the subscriber-side architecture overview.
- **Callers** — any existing code that called `OnInsert` before `UseConsumer` must reorder. No semantic change for callers that already had the canonical order. No change for callers using `AddChangeTracking` who wrote handlers after the consumer binding.
