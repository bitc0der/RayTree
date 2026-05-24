## ADDED Requirements

### Requirement: Topic probe re-runs on reconnect
When `WaitForTopic = true` AND the Kafka publisher or consumer enters a connection-recovery cycle (per the `connection-recovery` capability), the plugin SHALL re-run the same metadata probe against the rebuilt admin client before exposing the new producer/consumer handle to callers. The probe SHALL use the same `TopicWaitInterval` and `TopicWaitTimeout` values configured at construction. A timeout exhaustion during reconnect SHALL be treated as a recovery failure and SHALL propagate to the recovery strategy, which records the outcome as `exhausted`.

#### Scenario: Publisher re-probes topic after fatal-error rebuild
- **WHEN** `KafkaPublisher` was constructed with `WaitForTopic = true`
- **AND** a rebuild is triggered by an `Error.IsFatal = true` event
- **THEN** the publisher SHALL run the topic-wait probe via a fresh `IAdminClient` before the new `IProducer` is exposed to `PublishAsync` callers.

#### Scenario: Consumer re-probes topic after fatal-error rebuild
- **WHEN** `KafkaConsumer` was constructed with `WaitForTopic = true`
- **AND** a rebuild is triggered on the poll thread by an `Error.IsFatal = true` `KafkaException`
- **THEN** the consumer SHALL run the topic-wait probe via a fresh `IAdminClient` before invoking `Subscribe` on the new `IConsumer`.

#### Scenario: Probe timeout during reconnect surfaces as exhausted recovery
- **WHEN** a reconnect-time probe exceeds `TopicWaitTimeout`
- **THEN** the probe SHALL throw and the surrounding recovery cycle SHALL record `raytree.connection.recoveries` with `outcome = "exhausted"`
- **AND** an `Error` log SHALL be emitted by the recovery layer (not duplicated by the topic-wait layer).
