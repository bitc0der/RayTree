## Why

The current fluent builder allows only one handler per entity + action combination (`.OnInsert<T>`, `.OnUpdate<T>`, `.OnDelete<T>`, `.OnChange<T>`); registering a second handler silently replaces the first. Real applications need to fan out a single change event to multiple independent consumers (e.g., send a notification AND update a read model on insert) without coupling those concerns into one handler.

## What Changes

- `OnInsert<TEntity>`, `OnUpdate<TEntity>`, `OnDelete<TEntity>`, and `OnChange<TEntity>` on `IEntityBuilder<TEntity>` and `IChangeSubscriberBuilder` SHALL accumulate handlers rather than replace them.
- `ChangeSubscriber` SHALL invoke all registered handlers for a matching entity + action type in registration order, awaiting each before moving to the next.
- Retry and `SkipOnFailure` semantics apply per-handler (each handler is independently retried and can independently fail or be skipped).
- The dedup mark-before-process / revert-on-failure protocol remains unchanged; a failure in any handler that is not skipped reverts the dedup mark for the whole message.

## Capabilities

### New Capabilities

- `multiple-change-handlers`: Support registering multiple handlers per entity type and action type via the fluent builder; all handlers are invoked in order for each matching message.

### Modified Capabilities

- `subscriber-configuration`: The handler registration requirements change — calling `OnInsert<T>` (or any action method) more than once on the same entity MUST accumulate handlers, not overwrite. The existing code-example in the spec must be updated to show multiple handlers.

## Impact

- **`IEntityBuilder<TEntity>`** (`src/RayTree.Core`) — `OnInsert`, `OnUpdate`, `OnDelete`, `OnChange` signatures unchanged but semantics change to append.
- **`ChangeSubscriberBuilder` / `ChangeTrackingBuilder`** — internal handler storage changes from a single `Func` to `List<Func>` per (entity type, change type) key.
- **`ChangeSubscriber`** — dispatch loop iterates over the handler list; retry logic wraps each handler individually.
- **`IChangeSubscriberBuilder`** (if it exposes `OnXxx` directly) — same append semantics.
- No breaking API signature changes; existing single-handler call-sites continue to work as-is.
