## ADDED Requirements

### Requirement: Topic probe re-runs on fatal-error rebuild
When `WaitForTopic = true` AND a Kafka publisher or consumer rebuilds its native handle in response to a fatal error (per the `connection-recovery` capability), the plugin SHALL re-run the same metadata probe before exposing the new handle. The probe SHALL use the same `TopicWaitInterval` and `TopicWaitTimeout` values configured at construction. A timeout exhaustion during reconnect SHALL terminate the rebuild loop, record `raytree.connection.recoveries{outcome="exhausted"}`, and surface the underlying `KafkaException` to subsequent caller-facing calls.

#### Scenario: Publisher re-probes topic after fatal-error dispose
- **WHEN** `KafkaPublisher`'s error handler disposes the producer after a fatal error
- **AND** the next `PublishAsync` triggers `GetProducerAsync`
- **THEN** the producer build path SHALL re-run the topic-wait probe via a fresh `IAdminClient` before the new `IProducer` is exposed.

#### Scenario: Consumer re-probes topic after fatal-error rebuild
- **WHEN** `KafkaConsumer`'s poll thread catches a fatal `KafkaException` and enters the rebuild loop
- **THEN** the rebuild SHALL re-run the topic-wait probe via a fresh `IAdminClient` before invoking `Subscribe` on the new `IConsumer`.

#### Scenario: Probe timeout during reconnect surfaces as exhausted recovery
- **WHEN** a reconnect-time topic probe exceeds `TopicWaitTimeout`
- **THEN** the rebuild SHALL terminate with `raytree.connection.recoveries{outcome="exhausted"}` and surface the underlying `KafkaException` to subsequent caller-facing calls (publisher: next `PublishAsync` throws; consumer: poll thread exits and `StopAsync` observes the failure).
