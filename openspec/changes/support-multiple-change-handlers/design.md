## Context

`ChangeSubscriber` stores handlers in `Dictionary<Type, List<HandlerRegistration>>` and dispatches them in a `foreach` loop inside `ProcessMessageAsync`. Multiple handlers per entity + action already work at the infrastructure level, but the model has two real problems:

1. **Unspecified semantics.** XML doc comments say "registers a handler" with no statement about accumulation; the subscriber-configuration spec shows only one handler per action; there are no tests for the multi-handler path.
2. **Broken at-least-once with broker transports.** Both `RabbitMqConsumer` and `KafkaConsumer` ACK / commit the broker delivery **before** handing the envelope to the subscriber (`RabbitMqConsumer.cs:81`, `KafkaConsumer.cs:109`). When a handler exhausts retries and `ProcessMessageAsync` reverts the dedup mark, no redelivery occurs — the broker has already moved on. The "dedup revert → retry" guarantee that works for InMemoryQueue is silently false for production transports.

The narrow "make accumulation explicit" change addresses (1) but leaves (2) unresolved, and (2) becomes more visible when callers register two handlers and expect handler isolation.

## Goals / Non-Goals

**Goals:**
- Two handler-dispatch strategies — `Shared` (today's in-process foreach) and `Isolated` (one broker subscription per named handler) — with semantics specified, tested, and chosen at consumer-binding time.
- Compile-time enforcement of the mode/name contract: `UseConsumer` exposes only anonymous handlers, `UseConsumerFactory` exposes only named handlers. No way to mix.
- At-least-once retry-through-broker actually works in Isolated mode, because each named handler has its own delivery / ACK lifecycle.
- Existing `Shared` behavior is preserved bit-for-bit for callers that don't opt in.

**Non-Goals:**
- Rewriting Rabbit/Kafka consumer ACK semantics for Shared mode. `Shared` keeps today's ACK-before-process behavior; the dedup-revert promise is documented as best-effort and InMemory-strong.
- Parallel handler dispatch within `Shared` mode. Handlers remain sequential in registration order.
- Cross-handler ordering in Isolated mode. Each handler runs independently; no guarantee that handler A finishes before handler B starts for the same message.
- Per-handler dedup state in Shared mode. Shared mode's dedup is message-scoped; Isolated mode's dedup is `(message, handlerName)`-scoped — but these are not configurable.

## Decisions

### Mode is selected by the consumer-binding method, not a separate setting

`IEntityBuilder<TEntity>.UseConsumer(IQueueConsumer)` returns `ISharedHandlerBuilder<TEntity>`. `IEntityBuilder<TEntity>.UseConsumerFactory(Func<string, IQueueConsumer>)` returns `IIsolatedHandlerBuilder<TEntity>`. Handler registration methods (`OnInsert` etc.) live only on the post-fork builders, with shapes appropriate to the mode (anonymous overloads on `ISharedHandlerBuilder`, mandatory-name overloads on `IIsolatedHandlerBuilder`).

**Why this over a `HandlerMode` enum + single builder?** The type system enforces the contract. Calling `OnInsert(handler)` (anonymous) under Isolated mode is impossible to write — the overload simply does not exist on `IIsolatedHandlerBuilder<TEntity>`. Same for `UseConsumer` after committing to Isolated. The only check that remains at runtime is duplicate `(action, name)` pairs within an entity, which types cannot detect.

**Why this over two `ForEntity` methods (`ForEntity` / `ForIsolatedEntity`)?** A single entry point keeps the builder cohesive. The mode decision happens at the natural causal point — when the user picks how to bind the consumer — not as a separate up-front declaration. Discovery is preserved because both consumer-binding methods surface together in IntelliSense on `IEntityBuilder<TEntity>`.

**Alternative rejected: `OnInsert(string? name, handler)` with nullable name on a single builder.** Hides the mode decision; allows `OnInsert(null, ...)` followed by `OnInsert("x", ...)` to silently mean something incoherent. Two interfaces draw the line clearly.

### `Shared` mode keeps today's runtime — semantics-only change

Under `Shared`:
- `EntitySubscriberBuilder<TEntity>` accumulates handlers in its `List<(ChangeType?, Handler)>` (already does today).
- `ChangeSubscriber._handlers[typeof(TEntity)]` is a `List<HandlerRegistration>` (already today).
- Dispatch is the existing `foreach (var registration in matchingHandlers) await InvokeWithRetryAsync(...)`.
- Dedup key is `correlationId`.
- A handler exhausting retries with `SkipOnFailure = false` reverts the message-level dedup mark; on redelivery (where applicable) every handler re-runs. Handlers must be idempotent.

The only Shared-mode code change is removing `OnInsert/OnUpdate/OnDelete/OnChange` from `IEntityBuilder<TEntity>` and surfacing them on `ISharedHandlerBuilder<TEntity>` instead — purely an interface refactor.

### `Isolated` mode: one consume loop per `(entity, handlerName)`

The consumer factory `Func<string, IQueueConsumer>` is invoked once per registered handler name at `Build()` time. The framework holds a `Dictionary<(Type, string), IQueueConsumer>` and starts one consume loop per entry in `ChangeTrackingHostedService.StartAsync`. Each loop:
1. Reads envelopes from its dedicated consumer.
2. Checks dedup with key `$"{correlationId}:{handlerName}"`.
3. Deserializes the envelope (using the entity's serializer/compressor).
4. Invokes exactly **one** typed handler — the one this loop owns — through `InvokeWithRetryAsync`.
5. On success: commits/ACKs via the consumer (transport-specific, see "Broker semantics" below).
6. On failure (retries exhausted, `SkipOnFailure = false`): reverts dedup, throws — the consumer's own NACK/seek path triggers broker redelivery.

A new private method `ProcessIsolatedMessageAsync(envelope, entityType, handlerName, ct)` mirrors `ProcessMessageAsync` but takes the handler name and dispatches to exactly one registration.

**Why per-`(entity, handlerName)` and not per-handler-instance?** Across `OnInsert("x", h1)` + `OnUpdate("x", h2)` the same handler name applies to two action types but should share a single consumer (one subscription stream, filtered by `ChangeType` on the receiving side). One consume loop per name; the loop selects the registration matching the inbound `ChangeType`.

### Dedup key derivation

`Shared` mode → `dedupKey = correlationId.ToString()` (unchanged).
`Isolated` mode → `dedupKey = $"{correlationId}:{handlerName}"`.

`IDeduplicationStore` is untouched — it still takes a string. Per-handler isolation comes from the key shape, not a new store contract. The two key shapes are non-overlapping (the colon separator never appears in a GUID), so a single dedup store can be safely shared across both modes and across entities.

**Why not key derivation in the store?** Keeping the store ignorant of mode keeps it simple and substitutable. A Redis-backed store, a SQL-backed store, an InMemory store — all work without knowing what's encoded in the key.

### Plugin contract — `IQueueConsumer` does not change

Existing plugins (RabbitMQ, Kafka, InMemory) work in Shared mode unchanged. For Isolated, the user-supplied factory closes over per-name configuration:

```csharp
.UseConsumerFactory(name => new KafkaConsumer(
    options with { GroupId = $"orders-{name}" }, loggerFactory))
```

The plugin is unaware of "isolated" as a concept. It just constructs one consumer object per call, distinguished by whatever the user encodes into its options. This keeps the change additive at the plugin layer — no new methods on `IQueueConsumer`, no new abstract base classes.

**InMemoryQueue gets a broadcast variant.** A new `InMemoryBroadcastQueue` exposes `Subscribe()` returning a fresh `IQueueConsumer` that reads from its own inner `Channel<MessageEnvelope>`. The publisher writes to all inner channels. This is the only plugin requiring new code for Isolated mode; brokers handle fan-out themselves.

### Broker semantics in Isolated mode

The pre-existing ACK-before-process behavior in `RabbitMqConsumer` and `KafkaConsumer` is documented as a known limitation of the current consumer implementations and **does not change in this PR**. In Isolated mode the limitation becomes per-handler (each handler's consumer ACKs early), but the dedup-per-handler key already prevents double-processing.

Fixing ACK ordering is filed as a follow-up change (`consumer-ack-after-handler`) because it requires `IQueueConsumer` to gain an explicit ACK callback — a significant contract change orthogonal to handler-mode selection.

In the meantime: callers who need true broker-driven retry in Isolated mode use `SkipOnFailure = false` and rely on the dedup revert + manual DLQ patterns. This is documented in the spec's risk section.

### Handler ordering and identity

- **Shared mode**: handlers invoke in registration order, sequentially. This is observable behavior and tested.
- **Isolated mode**: handlers run in parallel across names (no cross-handler ordering). Within a single named consumer, the messages from that consumer are processed sequentially (existing `MaxDegreeOfParallelism` semantics apply per-loop).
- Handler **name** in Isolated mode is the stable identity. It maps to broker topology (`GroupId`, queue name) via the user's factory. Renaming a handler at deployment time is equivalent to creating a new subscription — the old name's offsets/messages stay; the new name starts fresh. Documented as a deployment caveat.

### Builder validation timing

- Anonymous overloads only on `ISharedHandlerBuilder<TEntity>` → compile-time check (no `OnInsert(handler)` reachable from Isolated).
- Named overloads only on `IIsolatedHandlerBuilder<TEntity>` → compile-time check.
- `UseConsumer` and `UseConsumerFactory` are both on `IEntityBuilder<TEntity>` — calling both is structurally impossible because each returns a different post-fork interface, but a determined caller using local variables could try; that scenario throws `InvalidOperationException` at `Build()`.
- Duplicate `(action, handlerName)` within an Isolated entity → `InvalidOperationException` at `Build()`.
- Empty handler list under either mode → no error; entity is effectively publisher-only.
- `Isolated` factory returning `null` for any name → `InvalidOperationException` at `Build()` with the name in the message.
- `Isolated` factory returning the same instance for two different names → `InvalidOperationException` at `Build()` (each handler needs an independent ACK lifecycle).

## Risks / Trade-offs

- **Breaking API change**: callers that registered handlers before `UseConsumer` (legal today) won't compile. → Mitigation: this library is pre-1.0; the fix is mechanical (reorder lines); CHANGELOG entry + migration note in the proposal.
- **Pre-existing ACK-before-process broker behavior carries into Isolated mode**: at-least-once retry doesn't fire from the broker today. → Mitigation: documented in spec; per-handler dedup keys still prevent double-processing; follow-up change tracked for ACK-after-handler.
- **Resource cost in Isolated mode**: N consumer groups (Kafka) / N queues (Rabbit) per entity instead of one. → Accepted trade-off for handler isolation. Documented in the cheat-sheet section of the spec.
- **Handler name stability is a deployment concern**: renaming a handler resets its subscription. → Documented as a deployment caveat. Recommend treating handler names as part of the public contract of the service.
- **Increased public-API surface**: two new interfaces (`ISharedHandlerBuilder`, `IIsolatedHandlerBuilder`). → Each is small (4 methods), maps directly to one mode, and replaces handler methods that were previously on `IEntityBuilder` — net surface growth is +4 methods.
- **`UseSubscriberOptions` lives on the entry interface, before the fork**: ergonomic ordering is "options → consumer → handlers" instead of the more natural "consumer → handlers → options". → Acceptable; XML doc comments + an example in the spec call this out.
