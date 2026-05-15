## ADDED Requirements

### Requirement: Publisher outbox metrics are emitted per entity type
The system SHALL emit `System.Diagnostics.Metrics` instruments for every outbox publishing operation. All publisher instruments SHALL be tagged with `entity_type` (the simple class name of the entity). Instruments SHALL be registered on a `Meter` named `"RayTree"`.

#### Scenario: Message published successfully
- **WHEN** `OutboxPublisherService` successfully publishes a change and marks it published
- **THEN** the `raytree.outbox.messages.published` counter is incremented by 1 with `entity_type` and `change_type` tags

#### Scenario: Message publish fails after all retries
- **WHEN** `OutboxPublisherService` exhausts all retry attempts for a change
- **THEN** the `raytree.outbox.messages.failed` counter is incremented by 1 with `entity_type` tag

#### Scenario: Batch size is recorded
- **WHEN** `OutboxPublisherService` retrieves a batch of unpublished changes
- **THEN** the `raytree.outbox.batch.size` histogram is updated with the count of changes in that batch, tagged with `entity_type`

#### Scenario: Publish duration is recorded
- **WHEN** `OutboxPublisherService` completes publishing a single change (including retries)
- **THEN** the `raytree.outbox.publish.duration` histogram is updated with the elapsed milliseconds, tagged with `entity_type`

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
- **THEN** the `raytree.subscriber.messages.deduplicated` counter is incremented by 1 with `entity_type` tag

#### Scenario: Message skipped due to unknown entity type
- **WHEN** `ChangeSubscriber.ProcessMessageAsync` cannot resolve the entity type from the envelope
- **THEN** the `raytree.subscriber.messages.skipped` counter is incremented by 1 with `entity_type` tag and `reason` tag set to `"unknown_type"`

#### Scenario: Message skipped due to no matching handlers
- **WHEN** `ChangeSubscriber.ProcessMessageAsync` finds no handlers registered for the resolved entity type and change type
- **THEN** the `raytree.subscriber.messages.skipped` counter is incremented by 1 with `entity_type` tag and `reason` tag set to `"no_handler"`

#### Scenario: Handler exhausts all retries
- **WHEN** a handler invoked from `InvokeWithRetryAsync` throws on its final attempt
- **THEN** the `raytree.subscriber.handler.failures` counter is incremented by 1 with `entity_type` tag

#### Scenario: Handler retried after transient failure
- **WHEN** a handler invoked from `InvokeWithRetryAsync` throws but has remaining retry attempts
- **THEN** the `raytree.subscriber.handler.retries` counter is incremented by 1 with `entity_type` tag

#### Scenario: Processing duration is recorded
- **WHEN** `ChangeSubscriber.ProcessMessageAsync` completes processing a message (success or SkipOnFailure drop)
- **THEN** the `raytree.subscriber.processing.duration` histogram is updated with the elapsed milliseconds, tagged with `entity_type` and `change_type`

---

### Requirement: Metrics are no-op when no meter factory is provided
The system SHALL produce no observable side effects from metrics calls when `IMeterFactory` is not supplied to the builder.

#### Scenario: Builder used without meter factory
- **WHEN** `ChangeTrackingBuilder` is constructed without providing an `IMeterFactory`
- **THEN** all metrics calls in `OutboxPublisherService` and `ChangeSubscriber` execute without error and without emitting any measurements

---

### Requirement: Hosting package exposes meter name via `AddRayTreeMetrics`
The system SHALL provide an extension method `AddRayTreeMetrics(this IMetricsBuilder builder)` in `RayTree.Hosting` that registers the `"RayTree"` meter name with the metrics builder.

#### Scenario: OTel metrics wiring via hosting extension
- **WHEN** `services.AddOpenTelemetry().WithMetrics(b => b.AddRayTreeMetrics())` is called during application startup
- **THEN** the OTel `MeterProvider` subscribes to instruments emitted by RayTree and exports them through the configured exporter
