## ADDED Requirements

### Requirement: Topology probe re-runs on reconnect
When `WaitForTopology = true` AND the RabbitMQ publisher or consumer enters a connection-recovery cycle (per the `connection-recovery` capability), the plugin SHALL re-run the same passive-declare probe(s) against the rebuilt channel before exposing the new channel to callers. The probe SHALL use the same `TopologyWaitInterval` and `TopologyWaitTimeout` values configured at construction. A timeout exhaustion during reconnect SHALL be treated as a recovery failure and SHALL propagate to the recovery strategy, which records the outcome as `exhausted`.

#### Scenario: Publisher re-probes exchange after reconnect
- **WHEN** `RabbitMqPublisher` was constructed with `WaitForTopology = true` AND `DeclareExchange = false`
- **AND** a reconnect cycle is triggered
- **THEN** the publisher SHALL invoke `ExchangeDeclarePassiveAsync` against the new channel and retry on `NOT_FOUND` per the existing topology-wait rules before the new channel is exposed for `PublishAsync`.

#### Scenario: Consumer re-probes queue and exchange after reconnect
- **WHEN** `RabbitMqConsumer` was constructed with `WaitForTopology = true` AND `DeclareQueue = false` AND `ExchangeName` is non-empty
- **AND** a reconnect cycle is triggered
- **THEN** the consumer SHALL probe the queue with `QueueDeclarePassiveAsync` and the exchange with `ExchangeDeclarePassiveAsync` against the new channel before re-issuing `BasicConsumeAsync`.

#### Scenario: Probe timeout during reconnect surfaces as exhausted recovery
- **WHEN** a reconnect-time probe exceeds `TopologyWaitTimeout`
- **THEN** the probe SHALL throw and the surrounding recovery cycle SHALL record `raytree.connection.recoveries` with `outcome = "exhausted"`
- **AND** an `Error` log SHALL be emitted by the recovery layer (not duplicated by the topology-wait layer).
