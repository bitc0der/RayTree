## REMOVED Requirements

### Requirement: Connection-recovery instruments are emitted
**Reason**: The connection-recovery metrics are removed entirely. Recovery is now observed through logs only (Postgres/Kafka retry, recovery, and exhaustion logs; RabbitMQ publisher `Warning`/`Information` logs). The four `raytree.connection.*` instruments and the three `RayTreeMeter` facade methods that emitted them (`RecordConnectionDisconnect`, `RecordConnectionRecovery`, `RegisterConnectionStateGauge`) no longer exist, and every plugin emission call site is removed.

**Migration**: Dashboards, alerts, and OTel views referencing `raytree.connection.disconnects`, `raytree.connection.recoveries`, `raytree.connection.recovery.duration`, or `raytree.connection.state` must be removed — those series cease to exist. To observe disconnect/recovery activity, consume the recovery log entries instead (filter on `{Component}` / `{Endpoint}` structured properties; recovery duration is available as the `{Duration}` property on the "recovered" `Information` log). No replacement metric is provided.
