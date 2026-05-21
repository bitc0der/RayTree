## Why

In microservice deployments, the service that *owns* a RabbitMQ exchange or queue (declares it) is frequently not the same service that publishes to it or consumes from it. Today, when `RabbitMqPublisher` is configured with `DeclareExchange = false` (or `RabbitMqConsumer` with `DeclareQueue = false`, or any consumer that binds to an externally-owned exchange), `InitializeAsync` fails immediately with an AMQP `NOT_FOUND` (404) channel exception if the topology is not yet present. This forces startup ordering between services and turns transient bootstrap races into permanent crashes.

We need an opt-in retry loop so a publisher/consumer can wait for topology owned by another service to come online, instead of crashing on first miss.

## What Changes

- Add an opt-in *topology wait* mode to `RabbitMqPublisherOptions` and `RabbitMqConsumerOptions`. When enabled and `Declare*` is `false`, initialization SHALL probe the relevant exchange/queue (and exchange-for-binding on the consumer) and retry on `NOT_FOUND` until it appears or the timeout/attempt budget is exhausted.
- Add configuration knobs: `WaitForTopology` (bool, default `false` — opt-in, no behaviour change for existing callers), `TopologyWaitInterval` (TimeSpan, default 5 s), `TopologyWaitTimeout` (TimeSpan?, default `null` — wait indefinitely until cancelled).
- Use AMQP *passive* declares (`ExchangeDeclarePassiveAsync` / `QueueDeclarePassiveAsync`) for probing so we never inadvertently auto-create topology we explicitly said we wouldn't own.
- Only `NOT_FOUND` (404) channel errors trigger retry; connection-level failures, permission errors, and other channel errors propagate immediately so genuine misconfiguration is still surfaced fast.
- Each retry uses a fresh channel — RabbitMQ closes the channel on any channel-level exception, so reusing it would fail on the next probe.
- Log topology-wait attempts at `Debug`, the first `NOT_FOUND` at `Information` (so operators see a one-time "waiting for X" message), and exhaustion at `Error`. Recovery (probe succeeded after one or more misses) logs at `Information`.
- Update `CLAUDE.md` to document the new options and the topology-ownership pattern they enable.

## Capabilities

### New Capabilities
- `rmq-topology-wait`: An opt-in wait/retry loop in `RabbitMqPublisher` / `RabbitMqConsumer` `InitializeAsync` that tolerates `NOT_FOUND` on externally-owned exchanges and queues until they appear.

### Modified Capabilities
<!-- None. `auto-storage-init` covers the "this service owns and creates" path; the new capability covers the orthogonal "this service does not own and must wait" path. -->

## Impact

- **Code**: `src/RayTree.Plugins.RabbitMQ/RabbitMqPublisher.cs`, `src/RayTree.Plugins.RabbitMQ/RabbitMqPublisherOptions.cs`, `src/RayTree.Plugins.RabbitMQ/RabbitMqConsumer.cs`, `src/RayTree.Plugins.RabbitMQ/RabbitMqConsumerOptions.cs`. A small internal helper (`TopologyProbe` or inline) for the probe-and-retry loop.
- **APIs**: Three new public properties on each of `RabbitMqPublisherOptions` and `RabbitMqConsumerOptions`. No breaking changes — defaults preserve current behaviour exactly.
- **Tests**: `tests/RayTree.Plugins.RabbitMQ.Tests` gains coverage for: (a) publisher waits then succeeds when exchange appears late, (b) consumer waits then succeeds when queue and bound exchange appear late, (c) timeout exhaustion surfaces the underlying `NOT_FOUND`, (d) non-`NOT_FOUND` errors propagate immediately without retry, (e) default (opt-out) behaviour is unchanged.
- **Dependencies**: None new — `ExchangeDeclarePassiveAsync` / `QueueDeclarePassiveAsync` already exist on `RabbitMQ.Client.IChannel`.
- **Docs**: `CLAUDE.md` RabbitMQ rows updated to describe `WaitForTopology` and the microservice topology-ownership scenario it enables.
