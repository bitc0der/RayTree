## ADDED Requirements

### Requirement: Outbox write rate is observable
The system SHALL emit a counter `raytree.outbox.writes` each time `EntityChangeTracker.TrackInsertAsync`, `TrackUpdateAsync`, or `TrackDeleteAsync` completes successfully. The counter SHALL be tagged with `entity_type` and `change_type`.

#### Scenario: Insert is tracked
- **WHEN** `EntityChangeTracker.TrackInsertAsync` returns successfully
- **THEN** the `raytree.outbox.writes` counter is incremented by 1 with `entity_type` and `change_type="Insert"` tags

#### Scenario: Failed write is not counted
- **WHEN** the underlying `IOutbox.WriteAsync` throws and the tracker propagates the exception
- **THEN** `raytree.outbox.writes` is not incremented

---

### Requirement: Outbox queue depth is observable as a gauge
The system SHALL expose an observable gauge `raytree.outbox.pending` that reports the count of unpublished outbox records per registered entity type. Each measurement SHALL be tagged with `entity_type`.

#### Scenario: Gauge sampled by OTel collection
- **WHEN** an OTel `MeterProvider` performs a collection tick
- **THEN** the `RayTreeMeter` callback queries `IOutbox.GetPendingCountAsync` for each registered entity type and emits one observation per entity type with the returned count

#### Scenario: Gauge reports zero on empty outbox
- **WHEN** no unpublished records exist for an entity type at collection time
- **THEN** the gauge emits a measurement of 0 with the `entity_type` tag

---

### Requirement: Publisher outbox metrics are emitted per entity type
The system SHALL emit `System.Diagnostics.Metrics` instruments for every outbox publishing operation. All publisher instruments SHALL be tagged with `entity_type`. Instruments tied to a specific change SHALL also carry `change_type`. All instruments SHALL be registered on a `Meter` named `"RayTree"`.

#### Scenario: Message published successfully
- **WHEN** `OutboxPublisherService` publishes a change and marks it published
- **THEN** the `raytree.outbox.messages.published` counter is incremented by 1 with `entity_type` and `change_type` tags

#### Scenario: Message publish fails after all retries
- **WHEN** `OutboxPublisherService` exhausts all retry attempts for a change
- **THEN** the `raytree.outbox.messages.failed` counter is incremented by 1 with `entity_type` and `change_type` tags

#### Scenario: Batch size is recorded
- **WHEN** `OutboxPublisherService` retrieves a batch of unpublished changes
- **THEN** the `raytree.outbox.batch.size` histogram records the count of changes in that batch, tagged with `entity_type`

#### Scenario: Single-attempt publish duration is recorded
- **WHEN** `OutboxPublisherService` completes one publish attempt (whether it succeeded or threw)
- **THEN** the `raytree.outbox.publish.duration` histogram records the elapsed seconds for that attempt, tagged with `entity_type` and `change_type`, with unit `s`

#### Scenario: Attempts-to-success is recorded on success
- **WHEN** `OutboxPublisherService` successfully publishes a change after N total attempts (N ≥ 1)
- **THEN** the `raytree.outbox.publish.attempts` histogram records the value N tagged with `entity_type`

#### Scenario: End-to-end outbox lag is recorded
- **WHEN** `OutboxPublisherService` successfully publishes a change with `Timestamp = t₀`
- **THEN** the `raytree.outbox.lag.duration` histogram records `(now - t₀)` in seconds, tagged with `entity_type`, with unit `s`

#### Scenario: Payload size is recorded
- **WHEN** `OutboxPublisherService` publishes a `MessageEnvelope` with a compressed payload
- **THEN** the `raytree.outbox.payload.size` histogram records `Payload.Length` bytes, tagged with `entity_type` and `change_type`, with unit `By`

#### Scenario: Outbox cleanup records published records deleted
- **WHEN** `OutboxPublisherService.MaybeRunCleanupAsync` deletes published records
- **THEN** the `raytree.outbox.records.cleaned` counter is incremented by the number of records deleted, tagged with `entity_type`

#### Scenario: Stale unpublished records removed
- **WHEN** `OutboxPublisherService.MaybeRunCleanupAsync` removes stale unpublished records
- **THEN** the `raytree.outbox.stale_unpublished.removed` counter is incremented by the number of records removed, tagged with `entity_type`

---

### Requirement: Subscriber processing metrics are emitted per entity type
The system SHALL emit `System.Diagnostics.Metrics` instruments for every message received by `ChangeSubscriber`. Subscriber instruments SHALL be tagged with `entity_type`. Instruments tracking change outcomes SHALL additionally be tagged with `change_type` where the value is known.

#### Scenario: Message processed successfully
- **WHEN** `ChangeSubscriber.ProcessMessageAsync` dispatches a message to all matching handlers without error
- **THEN** the `raytree.subscriber.messages.processed` counter is incremented by 1 with `entity_type` and `change_type` tags

#### Scenario: Duplicate message rejected by deduplication
- **WHEN** `ChangeSubscriber.ProcessMessageAsync` receives a message whose `CorrelationId` is already marked processed
- **THEN** the `raytree.subscriber.messages.deduplicated` counter is incremented by 1 with `entity_type` and `change_type` tags

#### Scenario: Message skipped due to unknown entity type
- **WHEN** `ChangeSubscriber.ProcessMessageAsync` cannot resolve the entity type from the envelope
- **THEN** the `raytree.subscriber.messages.skipped` counter is incremented by 1 with `entity_type` tag and `reason="unknown_type"` tag

#### Scenario: Message skipped due to no matching handlers
- **WHEN** `ChangeSubscriber.ProcessMessageAsync` finds no handlers registered or matching the change type
- **THEN** the `raytree.subscriber.messages.skipped` counter is incremented by 1 with `entity_type`, `change_type`, and `reason="no_handler"` tags

#### Scenario: Handler exhausts all retries
- **WHEN** a handler invoked from `InvokeWithRetryAsync` throws on its final attempt and `SkipOnFailure=false`
- **THEN** the `raytree.subscriber.handler.failures` counter is incremented by 1 with `entity_type` and `change_type` tags

#### Scenario: Handler attempts-to-success is recorded
- **WHEN** a handler invoked from `InvokeWithRetryAsync` returns successfully after N total attempts (N ≥ 1)
- **THEN** the `raytree.subscriber.handler.attempts` histogram records the value N tagged with `entity_type`

#### Scenario: Single-attempt processing duration is recorded
- **WHEN** `ChangeSubscriber.ProcessMessageAsync` invokes a handler attempt (success or failure)
- **THEN** the `raytree.subscriber.processing.duration` histogram records the elapsed seconds for that attempt, tagged with `entity_type` and `change_type`, with unit `s`

#### Scenario: End-to-end handler lag is recorded
- **WHEN** `ChangeSubscriber.ProcessMessageAsync` completes handler dispatch successfully for an envelope with `Timestamp = t₀`
- **THEN** the `raytree.subscriber.lag.duration` histogram records `(now - t₀)` in seconds, tagged with `entity_type` and `change_type`, with unit `s`

---

### Requirement: Metrics are silently inactive when no listener is attached
The system SHALL produce zero observable side effects from instrument calls when no `MeterListener` (or OTel `MeterProvider`) is subscribed to the `"RayTree"` meter.

#### Scenario: No listener attached
- **WHEN** runtime services execute their instrumentation calls and no listener has subscribed to the `"RayTree"` meter
- **THEN** all calls return without throwing and without retaining measurement state

---

### Requirement: Hosting package exposes meter name via `AddRayTreeMetrics`
The system SHALL provide an extension method `AddRayTreeMetrics(this MeterProviderBuilder builder)` in `RayTree.Hosting` that registers the `"RayTree"` meter name on the OTel `MeterProvider`. The method SHALL be a thin pass-through that does not configure exporters, views, or bucket boundaries.

#### Scenario: OTel metrics wiring via hosting extension
- **WHEN** `services.AddOpenTelemetry().WithMetrics(b => b.AddRayTreeMetrics())` is called at startup
- **THEN** the OTel `MeterProvider` subscribes to all instruments emitted on the `"RayTree"` meter and exports them through the configured exporter

#### Scenario: All durations use seconds
- **WHEN** any `*.duration` histogram is created on the `"RayTree"` meter
- **THEN** its `unit` property is the string `"s"` per OTel semantic conventions
