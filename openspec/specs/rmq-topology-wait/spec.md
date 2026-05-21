### Requirement: Opt-in topology wait flag
The RabbitMQ publisher and consumer SHALL expose a `WaitForTopology` boolean option (default `false`) that, when `true`, causes `InitializeAsync` to wait for externally-owned RabbitMQ topology (exchanges and queues) to appear instead of failing immediately on `NOT_FOUND`.

#### Scenario: Default behaviour is unchanged
- **WHEN** `WaitForTopology` is not set (or set to `false`) on `RabbitMqPublisherOptions` or `RabbitMqConsumerOptions`
- **THEN** `InitializeAsync` SHALL behave exactly as before — a missing exchange, missing queue, or `QueueBind` to a missing exchange SHALL surface the underlying `OperationInterruptedException` on the first failed AMQP operation without any retry.

#### Scenario: Opt-in enables wait loop
- **WHEN** `WaitForTopology = true` is set on either options class
- **THEN** `InitializeAsync` SHALL probe the relevant topology with passive declares and retry on `NOT_FOUND` according to the configured interval and timeout.

### Requirement: Publisher waits for externally-owned exchange
When `RabbitMqPublisherOptions.WaitForTopology = true` AND `DeclareExchange = false`, `RabbitMqPublisher.InitializeAsync` SHALL probe the configured `ExchangeName` with `ExchangeDeclarePassiveAsync` and retry on `NOT_FOUND` until the exchange appears, the cancellation token is cancelled, or `TopologyWaitTimeout` (when set) elapses.

#### Scenario: Exchange appears after one or more probe attempts
- **WHEN** the exchange named in `ExchangeName` does not exist at the moment `InitializeAsync` is called but is declared by another service shortly after
- **THEN** the publisher SHALL retry the passive declare at intervals of `TopologyWaitInterval`
- **AND** SHALL complete `InitializeAsync` successfully once the passive declare succeeds
- **AND** SHALL log the first `NOT_FOUND` at `Information` level and the eventual recovery at `Information` level.

#### Scenario: `DeclareExchange = true` skips the wait
- **WHEN** `DeclareExchange = true` regardless of `WaitForTopology`
- **THEN** the publisher SHALL declare the exchange itself with `ExchangeDeclareAsync` and SHALL NOT perform a passive probe.

### Requirement: Consumer waits for externally-owned queue
When `RabbitMqConsumerOptions.WaitForTopology = true` AND `DeclareQueue = false`, `RabbitMqConsumer.InitializeAsync` SHALL probe the configured `QueueName` with `QueueDeclarePassiveAsync` and retry on `NOT_FOUND` until the queue appears, the cancellation token is cancelled, or `TopologyWaitTimeout` (when set) elapses. The probe SHALL occur before any `QueueBindAsync` or `BasicConsumeAsync` call.

#### Scenario: Queue appears after one or more probe attempts
- **WHEN** the queue named in `QueueName` does not exist when `InitializeAsync` is called
- **AND** another service declares it shortly after
- **THEN** the consumer SHALL retry the passive declare at intervals of `TopologyWaitInterval`
- **AND** SHALL proceed to `BasicConsumeAsync` once the passive declare succeeds.

#### Scenario: `DeclareQueue = true` skips the queue wait
- **WHEN** `DeclareQueue = true` regardless of `WaitForTopology`
- **THEN** the consumer SHALL declare the queue itself with `QueueDeclareAsync` and SHALL NOT perform a passive probe for the queue.

### Requirement: Consumer waits for binding-target exchange
When `RabbitMqConsumerOptions.WaitForTopology = true` AND `ExchangeName` is non-empty, `RabbitMqConsumer.InitializeAsync` SHALL probe `ExchangeName` with `ExchangeDeclarePassiveAsync` and retry on `NOT_FOUND` before calling `QueueBindAsync`, using the same interval and timeout as the queue probe.

#### Scenario: Exchange-for-binding appears after probe retries
- **WHEN** `ExchangeName` references an exchange that does not yet exist
- **THEN** the consumer SHALL retry the passive declare at intervals of `TopologyWaitInterval`
- **AND** SHALL invoke `QueueBindAsync` once the exchange exists.

#### Scenario: No `ExchangeName` configured
- **WHEN** `ExchangeName` is null or empty
- **THEN** the consumer SHALL NOT probe any exchange and SHALL skip `QueueBindAsync` (default-exchange routing path).

### Requirement: Retry only on `NOT_FOUND`
The topology wait loop SHALL retry only when the AMQP operation fails with reply code `404 NOT_FOUND`. All other channel-level exceptions, connection-level exceptions, and `OperationCanceledException` SHALL propagate immediately without retry.

#### Scenario: PRECONDITION_FAILED propagates immediately
- **WHEN** the broker rejects the passive declare with reply code `406 PRECONDITION_FAILED` (for example, the existing exchange has different arguments than expected)
- **THEN** `InitializeAsync` SHALL propagate the exception on the first attempt without further retries.

#### Scenario: ACCESS_REFUSED propagates immediately
- **WHEN** the broker rejects the operation with reply code `403 ACCESS_REFUSED`
- **THEN** `InitializeAsync` SHALL propagate the exception on the first attempt without further retries.

#### Scenario: Connection failure propagates immediately
- **WHEN** the TCP connection cannot be established or is dropped during initialization
- **THEN** the resulting connection-level exception SHALL propagate without retry.

### Requirement: Retry interval and timeout configuration
The publisher and consumer options SHALL expose `TopologyWaitInterval` (TimeSpan, default `5 seconds`) and `TopologyWaitTimeout` (TimeSpan?, default `null`). When `TopologyWaitTimeout` is non-null, the wait loop SHALL stop and rethrow the most recent `NOT_FOUND` exception once the elapsed time exceeds the timeout.

#### Scenario: Custom interval is honoured
- **WHEN** `TopologyWaitInterval = TimeSpan.FromSeconds(1)` is set
- **THEN** consecutive passive declare attempts SHALL be separated by approximately one second.

#### Scenario: Timeout exhaustion surfaces the underlying error
- **WHEN** `TopologyWaitTimeout = TimeSpan.FromSeconds(10)` is set
- **AND** the topology has not appeared after 10 seconds of probing
- **THEN** `InitializeAsync` SHALL throw the last observed `NOT_FOUND` exception.

#### Scenario: Null timeout means no ceiling
- **WHEN** `TopologyWaitTimeout = null`
- **THEN** the wait loop SHALL continue indefinitely until either the topology appears or the cancellation token is cancelled.

### Requirement: Cancellation token cancels the wait
The wait loop SHALL observe the `CancellationToken` passed into `InitializeAsync`. When the token is cancelled during a wait or between attempts, the loop SHALL throw `OperationCanceledException`.

#### Scenario: Cancellation during the inter-attempt delay
- **WHEN** the cancellation token is cancelled while the wait loop is sleeping between attempts
- **THEN** `InitializeAsync` SHALL throw `OperationCanceledException` rather than continuing.

### Requirement: Fresh channel per retry
Each passive-declare attempt SHALL be executed on a freshly created channel because RabbitMQ closes the channel on every channel-level exception. The persistent working channel held by the publisher/consumer SHALL be opened only after the probe(s) succeed.

#### Scenario: Channel is recreated after a NOT_FOUND
- **WHEN** a passive declare returns `NOT_FOUND` and closes the probe channel
- **THEN** the next attempt SHALL open a new channel from the existing connection before issuing the next passive declare.

### Requirement: Logging of topology wait
The plugin SHALL emit the following log entries when `WaitForTopology = true`:

- First `NOT_FOUND` per probed entity: `Information`, with the entity kind (exchange/queue) and name.
- Subsequent `NOT_FOUND` attempts for the same entity: `Debug`.
- Recovery (probe succeeds after one or more misses): `Information`.
- Timeout exhaustion: `Error`, immediately before rethrowing.

#### Scenario: First miss logged at Information
- **WHEN** the first passive declare for an entity returns `NOT_FOUND`
- **THEN** an `Information`-level log SHALL be emitted indicating the consumer/publisher is waiting for that entity by name.

#### Scenario: Recovery logged at Information
- **WHEN** a passive declare succeeds after at least one prior `NOT_FOUND`
- **THEN** an `Information`-level log SHALL be emitted indicating the topology became available.
