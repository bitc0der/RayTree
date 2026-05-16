## 1. API Documentation

- [ ] 1.1 Update XML doc comment on `IEntitySubscriberBuilder<TEntity>.OnInsert` to state that each call accumulates a new handler and does not replace any previously registered one
- [ ] 1.2 Update XML doc comments on `IEntitySubscriberBuilder<TEntity>.OnUpdate`, `OnDelete`, and `OnChange` with the same accumulate-not-replace wording
- [ ] 1.3 Update XML doc comments on `IEntityBuilder<TEntity>.OnInsert`, `OnUpdate`, `OnDelete`, and `OnChange` with the same accumulate-not-replace wording

## 2. Unit Tests — Multiple Handler Dispatch

- [ ] 2.1 Add test: two `OnInsert` handlers registered for the same entity — both invoked in registration order for an Insert message
- [ ] 2.2 Add test: `OnInsert` + catch-all `OnChange(null, ...)` registered for the same entity — an Insert message invokes both, in registration order
- [ ] 2.3 Add test: `OnInsert(handlerA)` and `OnUpdate(handlerB)` — Insert message invokes only A; Update message invokes only B
- [ ] 2.4 Add test: three handlers (A, B, C) registered for the same action type — all three invoked in A → B → C order

## 3. Unit Tests — Retry and Skip-On-Failure With Multiple Handlers

- [ ] 3.1 Add test: first handler succeeds, second handler fails then succeeds on retry — first handler invoked once, second invoked twice total
- [ ] 3.2 Add test: first handler succeeds, second handler exhausts retries with `SkipOnFailure = true` — second is skipped (no exception), third handler (if any) continues executing
- [ ] 3.3 Add test: first handler succeeds, second handler exhausts retries with `SkipOnFailure = false` — exception propagates, dedup mark is reverted

## 4. Unit Tests — Dedup Revert Semantics With Multiple Handlers

- [ ] 4.1 Add test: when second handler fails all retries (`SkipOnFailure = false`), `IDeduplicationStore.RevertProcessedAsync` is called exactly once with the message correlation ID
- [ ] 4.2 Add test: when second handler fails all retries but `SkipOnFailure = true`, `RevertProcessedAsync` is NOT called and the message is considered fully processed
