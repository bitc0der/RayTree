## Context

`ChangeSubscriber` stores handlers in `Dictionary<Type, List<HandlerRegistration>>`. The dispatch loop in `ProcessMessageAsync` already calls `foreach (var registration in matchingHandlers) await InvokeWithRetryAsync(...)`, so it already iterates every registered handler in order. `EntitySubscriberBuilder<TEntity>` accumulates calls to `OnInsert`/`OnUpdate`/`OnDelete`/`OnChange` in its own `List<(ChangeType?, Handler)>` field and transfers them all to the subscriber on `Apply`.

The runtime behavior is correct: multiple handlers per entity + action type already work. The gap is that this behavior is **undocumented** (XML doc comments say "registers a handler" without mentioning accumulation), **unspecified** (the subscriber-configuration spec's example shows only one handler per action and has no explicit requirement about multiple registrations), and **untested** (no tests exercise two or more handlers for the same entity + action).

## Goals / Non-Goals

**Goals:**
- Explicitly specify that `OnInsert`, `OnUpdate`, `OnDelete`, and `OnChange` accumulate handlers (each call adds another; none replaces an earlier one).
- Add tests covering multiple-handler dispatch, per-handler retry semantics, and dedup-revert behavior when one handler in a chain fails.
- Update XML doc comments on all handler-registration methods to state accumulate semantics clearly.
- Update the subscriber-configuration spec to include multiple-handler scenarios.

**Non-Goals:**
- Parallel handler invocation — handlers remain sequential in registration order.
- Per-handler dedup state — dedup is per-message; if any non-skipped handler exhausts retries, the whole message's dedup mark is reverted (at-least-once delivery is message-scoped).
- Ordered guarantee across different entity types or different action types.
- Any API signature changes — existing call-sites continue to work as-is.

## Decisions

### No runtime changes required

The dispatch infrastructure is already correct. `ChangeSubscriber.OnChange<TEntity>` appends to `_handlers[typeof(TEntity)]`; `EntitySubscriberBuilder.OnChange` appends to its internal list; `ProcessMessageAsync` iterates `matchingHandlers` with `foreach`. No production code in the hot path needs to change.

**Alternative considered**: Treat a second `OnInsert` call as a replacement (last-wins). Rejected — this would silently discard earlier handlers and break composition across modules that each register their own handler independently.

### Dedup revert scope is the whole message, not a single handler

If handler A succeeds and handler B then exhausts its retries and throws, `ProcessMessageAsync` reverts the dedup mark for the correlation ID. On redelivery, both A and B will run again. This is at-least-once delivery semantics applied at the message boundary, consistent with the existing model.

**Alternative considered**: Mark each handler independently with its own dedup key (`correlationId + handlerIndex`). Rejected — handler indices are not stable across deployments, and the additional store round-trips per handler negate the efficiency advantage of the outbox approach.

### `SkipOnFailure` applies per handler

`InvokeWithRetryAsync` already reads `SkipOnFailure` per handler and returns silently if the handler exhausts retries and `SkipOnFailure = true`. Later handlers in the list continue executing. Only when `SkipOnFailure = false` and retries are exhausted does the exception propagate up to `ProcessMessageAsync`, which then reverts the dedup mark for the whole message.

This is the existing behavior; the decision is to document and test it explicitly rather than change it.

## Risks / Trade-offs

- **At-least-once re-execution of successful handlers** → Accepted. At-least-once is the documented delivery guarantee of the outbox pattern. Handlers must be idempotent. This is called out in the spec scenarios.
- **Silent accumulation if `ForEntity<TEntity>` is called twice** — two separate `EntitySubscriberBuilder` instances are applied, which accumulates handlers but also allows overwriting queue/serializer/compressor settings from the first call. → This is an edge case; callers should prefer a single `ForEntity<TEntity>` block with multiple `OnInsert` calls. No change needed; behavior is acceptable and consistent with how the builder already works.
