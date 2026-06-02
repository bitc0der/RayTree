## REMOVED Requirements

### Requirement: Connection-recovery instruments are emitted
**Reason**: The connection-recovery metrics are removed entirely. Recovery is now observed through logs only (Postgres/Kafka retry, recovery, and exhaustion logs; RabbitMQ publisher `Warning`/`Information` logs). The four `raytree.connection.*` instruments and the three `RayTreeMeter` facade methods that emitted them (`RecordConnectionDisconnect`, `RecordConnectionRecovery`, `RegisterConnectionStateGauge`) no longer exist, and every plugin emission call site is removed.

**Migration**: Dashboards, alerts, and OTel views referencing `raytree.connection.disconnects`, `raytree.connection.recoveries`, `raytree.connection.recovery.duration`, or `raytree.connection.state` must be removed — those series cease to exist. To observe disconnect/recovery activity, consume the recovery log entries instead (filter on `{Component}` / `{Endpoint}` structured properties; recovery duration is available as the `{Duration}` property on the "recovered" `Information` log). No replacement metric is provided.

## ADDED Requirements

### Requirement: Metric emission is not part of the public API
`RayTreeMeter` SHALL expose no public method for emitting or registering a metric. All emit/register members (`RecordPublishSuccess`, `RecordPublishFailure`, `RecordPayloadSize`, `RecordBatchSize`, `RegisterPendingGauge`) SHALL be `internal`, consumed only by `RayTree.Core` and by assemblies granted `InternalsVisibleTo` (`RayTree.Plugins.PostgreSQL`, `RayTree.EntityFrameworkCore`, and the test projects). `RayTreeMeter`'s public surface SHALL consist of `MeterName`, its constructors, `DefaultPendingCacheTtl`, and `Dispose()`. Metric *observation* SHALL remain public via the `"RayTree"` meter name and `RayTree.OpenTelemetry.AddRayTreeMetrics`.

#### Scenario: No public emit method exists
- **WHEN** a consumer outside Core and without `InternalsVisibleTo` references `RayTreeMeter`
- **THEN** no metric-emitting or gauge-registering method SHALL be accessible
- **AND** only `MeterName`, the constructors, `DefaultPendingCacheTtl`, and `Dispose()` SHALL be available.

#### Scenario: Privileged assemblies still emit internally
- **WHEN** `NotificationBasedPublisher` (in `RayTree.Plugins.PostgreSQL`, an `InternalsVisibleTo`-granted assembly) emits publish metrics on the NOTIFY fast-path
- **THEN** it SHALL call the `internal` `RecordPublishSuccess` / `RecordPublishFailure` / `RecordPayloadSize` / `RecordBatchSize` members
- **AND** the emitted instruments SHALL remain observable via `AddRayTreeMetrics`.
