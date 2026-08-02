## ADDED Requirements

### Requirement: Connection-recovery instruments are emitted
The system SHALL emit four `System.Diagnostics.Metrics` instruments on the `"RayTree"` meter that describe connection-recovery activity across every connection-bearing plugin:

- `raytree.connection.disconnects` — `Counter<long>`, tagged with `component` and `endpoint`. Incremented once each time a plugin observes a disconnect (Postgres connection-fault exception, Kafka fatal error, RabbitMQ `ConnectionShutdownAsync` with non-application initiator).
- `raytree.connection.recoveries` — `Counter<long>`, tagged with `component`, `endpoint`, and `outcome ∈ {"succeeded", "exhausted"}`. Incremented once per completed recovery cycle.
- `raytree.connection.recovery.duration` — `Histogram<double>`, unit `s`, tagged with `component`, `endpoint`, and `outcome`. Records wall-clock seconds elapsed for each completed recovery cycle (from first detection to completion).
- `raytree.connection.state` — `ObservableGauge<int>` emitting `1` (connected) or `0` (disconnected), tagged with `component` and `endpoint`. Sampled per OTel collection tick.

`component` values SHALL be drawn from the fixed set `{"rabbitmq.publisher", "rabbitmq.consumer", "kafka.publisher", "kafka.consumer", "postgres.notification", "postgres.outbox"}`. The `"postgres.outbox"` component is emitted by `OutboxPublisherService` and `NotificationBasedPublisher`'s fallback polling loop when an outbox call fails with a connection-fault classified by the `IOutbox` implementation. `endpoint` SHALL identify the broker host (`"{HostName}:{Port}"` for RabbitMQ, `BootstrapServers` for Kafka) or the LISTEN channel name (for Postgres) and SHALL be sourced from plugin configuration only — never from caller-supplied request data.

Suggested histogram bucket boundaries for `raytree.connection.recovery.duration` are `[0.1, 0.5, 1, 2, 5, 10, 30, 60, 120]` seconds.

#### Scenario: Disconnect increments counter with component tag
- **WHEN** `RabbitMqPublisher` observes a `ConnectionShutdownAsync` event with non-application initiator
- **THEN** `raytree.connection.disconnects` SHALL be incremented by 1 with `component = "rabbitmq.publisher"` and `endpoint` equal to `"{HostName}:{Port}"`.

#### Scenario: Successful recovery records duration with outcome="succeeded"
- **WHEN** a recovery cycle completes successfully for any participating component
- **THEN** `raytree.connection.recoveries` SHALL be incremented by 1 with `outcome = "succeeded"`
- **AND** `raytree.connection.recovery.duration` SHALL record the elapsed wall-clock seconds with the same tag set.

#### Scenario: Exhausted recovery records duration with outcome="exhausted"
- **WHEN** a Postgres or Kafka recovery cycle exhausts the configured `MaxAttempts` without success
- **THEN** `raytree.connection.recoveries` SHALL be incremented by 1 with `outcome = "exhausted"`
- **AND** `raytree.connection.recovery.duration` SHALL record the elapsed wall-clock seconds with the same tag set.

#### Scenario: State gauge reflects current connectivity per component
- **WHEN** an OTel `MeterProvider` performs a collection tick
- **THEN** `raytree.connection.state` SHALL emit one observation per registered `(component, endpoint)` pair
- **AND** the value SHALL be `1` when the component is connected and `0` when it is inside a recovery cycle.

#### Scenario: Duration histogram uses seconds
- **WHEN** `raytree.connection.recovery.duration` is created
- **THEN** its `unit` property SHALL be the string `"s"` per the existing duration-unit requirement.

#### Scenario: Outbox-side disconnect is tagged "postgres.outbox"
- **WHEN** `OutboxPublisherService.ProcessBatchAsync` catches a connection-fault exception and the outbox's `ConnectionComponent` is `"postgres.outbox"`
- **THEN** `raytree.connection.disconnects` SHALL be incremented with `component = "postgres.outbox"` and `endpoint = "{Host}:{Port}"` parsed from the connection string.
