## Context

RayTree's RabbitMQ plugin currently performs all topology declaration eagerly inside `InitializeAsync`:

- `RabbitMqPublisher`: opens a connection + channel, optionally calls `ExchangeDeclareAsync` (gated by `DeclareExchange`).
- `RabbitMqConsumer`: opens a connection + channel, optionally calls `QueueDeclareAsync` (gated by `DeclareQueue`), then `QueueBindAsync` (gated by `ExchangeName` being non-empty), then starts `BasicConsume`.

When `DeclareExchange = false` (publisher) or `DeclareQueue = false` (consumer), the caller is saying "another component owns this topology — don't auto-create it." But the moment any subsequent operation references a piece of topology that hasn't been declared yet (a `BasicPublish` to a non-existent exchange, a `QueueBind` to a non-existent exchange, or even a `BasicConsume` against a non-existent queue), RabbitMQ raises a channel-level exception with reply code `404 NOT_FOUND` and the channel becomes unusable.

In any deployment where the topology owner starts after a consumer or publisher — common in microservice systems where startup order is not strict, or where a configuration service / migration job declares topology — this turns into a startup crash that the operator must work around by sequencing services manually or by restarting until everything stabilises.

The fix is small in scope: an opt-in *wait* loop in `InitializeAsync` that probes the relevant topology with passive declares and retries on `NOT_FOUND` until it shows up or a budget expires. The existing "declare it myself" path is preserved untouched.

## Goals / Non-Goals

**Goals:**
- Let an operator deploy a RabbitMQ publisher or consumer that *consumes from* a topology owned elsewhere without requiring strict startup ordering.
- Preserve all current behaviour by default (opt-in flag, default `false`).
- Distinguish "topology not yet present" (retryable) from "genuine misconfiguration" (immediate failure) so misconfigured deployments still fail fast.
- Keep the implementation contained to the RabbitMQ plugin — no changes to `IQueuePublisher` / `IQueueConsumer` contracts, no changes to `EntityChangeTracker` lifecycle.

**Non-Goals:**
- Re-establishing topology after a *runtime* loss (e.g., the exchange is deleted while the publisher is connected). That is a much larger feature touching publisher confirms and connection-recovery wiring; this change is strictly about startup.
- Auto-creating topology that the caller said they didn't want to declare. We only *wait* — we never `Declare` (active) on a side that opted out.
- Adding retry to other plugins (Kafka, PostgreSQL). The corresponding failure modes are different and are out of scope here.
- Generalising into a cross-plugin "wait-for-dependency" abstraction. Premature — handle Kafka/Postgres equivalents when their real use cases arrive.

## Decisions

### Decision 1: Use AMQP passive declares for probing

We probe topology with `ExchangeDeclarePassiveAsync(name)` and `QueueDeclarePassiveAsync(name)`. Passive declares return success if the entity exists with any configuration; they return `NOT_FOUND` (and close the channel) if it doesn't. They never create anything.

**Alternatives considered:**
- *Reuse `ExchangeDeclareAsync` / `QueueDeclareAsync` (active) for probing.* Rejected — the active form would create the topology if missing, which directly contradicts the user's explicit choice to set `DeclareExchange = false` / `DeclareQueue = false`. The whole point of the opt-out is that the local service does not own that topology and may not have the configuration (durability, args, exchange type) to declare it correctly.
- *Skip the probe and let the first real operation fail.* Rejected for the consumer side — `BasicConsume` against a missing queue, or `QueueBind` to a missing exchange, both fail with `NOT_FOUND` but at a point where we've already pre-allocated a channel and registered a delivery handler. The recovery path is messier than just doing an explicit probe up-front. Passive declare is also cheap: a single round-trip with no broker-side persistence.

### Decision 2: Retry only on `NOT_FOUND` (404)

The retry loop catches `OperationInterruptedException` whose `ShutdownReason.ReplyCode == 404`. Everything else — auth failures (`ACCESS_REFUSED` 403), arg mismatches (`PRECONDITION_FAILED` 406), connection-level errors, `OperationCanceledException`, etc. — propagates immediately.

**Alternatives considered:**
- *Retry on any channel-level exception.* Rejected — `PRECONDITION_FAILED` typically means the topology exists but with arguments different from what the caller expected. That's a configuration bug, not a startup race, and looping on it would hide the problem indefinitely.
- *Make the predicate user-configurable.* Rejected for the first iteration — `NOT_FOUND` is the only error class this feature is intended to mask. If a real use case appears for a broader predicate we can add it later without breaking the narrower API.

### Decision 3: Open a fresh channel for each retry attempt

RabbitMQ closes the channel as part of any channel-level exception. The first failed probe leaves the existing `_channel` unusable. Each retry therefore opens a new channel from the existing connection. The connection itself stays — connection-level errors are not retried here.

**Alternatives considered:**
- *Reuse a single channel by reopening it after each failure.* Rejected — there is no "reopen" API on a closed `IChannel`; you have to call `_connection.CreateChannelAsync` again. So the cost is identical and adding indirection would only obscure that.
- *Use a single dedicated probe channel separate from the working channel.* Rejected — it would still need to be reopened after each `NOT_FOUND`, so it provides no win, and it doubles the channel count for a one-shot startup task.

### Decision 4: Default `WaitForTopology = false`

The new option defaults to `false`. Existing callers who never set it see exactly today's behaviour: a missing exchange/queue throws immediately. The opt-in keeps semantics explicit and avoids silently masking misconfigurations in deployments that *should* fail fast (e.g., dev environments where startup order is already enforced and a missing exchange is a real bug).

**Alternatives considered:**
- *Default `true`.* Rejected — would change the behaviour of every existing deployment on upgrade, including ones where today's fast-fail is the desired and expected behaviour. The cost of the explicit opt-in is one extra line of options configuration; the cost of an accidental default change is silent startup hangs.

### Decision 5: `TopologyWaitTimeout` defaults to `null` (no ceiling)

When `WaitForTopology = true`, the loop runs until topology appears or the caller's `CancellationToken` is cancelled. There is no built-in time budget. Operators who *do* want a hard ceiling set `TopologyWaitTimeout` explicitly.

**Rationale:** The most common deployment scenario is "wait for the dependency, however long it takes" — typically gated by an orchestrator's overall startup timeout (Kubernetes liveness/readiness, Docker Compose health checks). Adding a default ceiling would introduce a second, hidden timeout that operators have to discover and override.

**Alternatives considered:**
- *Default to a finite ceiling (e.g., 5 minutes).* Rejected for the reason above — it surprises operators with an arbitrary deadline that's not exposed in the orchestrator they're already using.
- *Default to a small attempt count (e.g., 12 attempts × 5s = 1 minute).* Same problem; rejected.

### Decision 6: Probing scope per side

| Side | What we probe (only when `WaitForTopology = true`) |
|---|---|
| Publisher, `DeclareExchange = false` | The configured `ExchangeName`. |
| Publisher, `DeclareExchange = true` | Nothing — we're declaring it ourselves. Wait flag is ignored. |
| Consumer, `DeclareQueue = false` | The configured `QueueName`. |
| Consumer, `DeclareQueue = true` | Nothing for the queue itself. |
| Consumer, `ExchangeName` set (any `DeclareQueue` value) | The exchange named in `ExchangeName`, *before* `QueueBindAsync`. |

This matches the existing logic in `RabbitMqConsumer.InitializeAsync` (which already conditions `QueueDeclareAsync` on `DeclareQueue` and `QueueBindAsync` on `!string.IsNullOrEmpty(ExchangeName)`). Each guarded step gets its own probe immediately before it; the probe is skipped when the corresponding declare is being performed locally.

## Risks / Trade-offs

- **[Indefinite hangs on missing topology when timeout is `null`]** → The default behaviour relies on the caller-supplied `CancellationToken` to bound the wait. `EntityChangeTracker.StartAsync(CancellationToken)` already propagates a token from `ChangeTrackingHostedService`, and the host's shutdown timeout will cancel it during a stuck startup. Operators who want a tighter bound set `TopologyWaitTimeout` explicitly. This is documented in `CLAUDE.md`.
- **[`NOT_FOUND` masking a real bug]** → If a publisher is configured against the wrong exchange name (typo) with `WaitForTopology = true`, it will wait forever. Mitigation: the first `NOT_FOUND` logs at `Information` ("waiting for exchange `X`"), so the typo is visible in startup logs. Operators who want a hard fail set a finite `TopologyWaitTimeout`.
- **[Extra channel churn]** → Each retry opens a fresh channel. At a 5-second interval over a 1-minute wait, that's 12 channel open/close cycles. RabbitMQ tolerates this trivially (channels are cheap), and the activity is one-shot at startup. No expected production impact.
- **[Behaviour change in existing tests / examples]** → Defaults are unchanged, so no existing test should break. The new tests live in `tests/RayTree.Plugins.RabbitMQ.Tests` and use Testcontainers (already in use for this project).
- **[Concurrent topology creation race]** → If the topology owner declares the exchange between our probe and the next operation, no harm done — the next operation succeeds. If it declares it concurrently with the same name but different arguments, that's a configuration bug on their side; we don't try to detect or correct it here.

## Migration Plan

This is purely additive — no migration steps for existing callers. Callers who want the new behaviour:

1. Set `RabbitMqPublisherOptions.WaitForTopology = true` (publisher side) and/or `RabbitMqConsumerOptions.WaitForTopology = true` (consumer side).
2. Optionally set `TopologyWaitInterval` (default 5 s) and `TopologyWaitTimeout` (default unlimited).
3. Ensure the `CancellationToken` passed to `tracker.StartAsync` is the one the host actually cancels on shutdown (already the case under `ChangeTrackingHostedService`).

Rollback: clear the flag — there is no persistent broker state introduced by this change.
