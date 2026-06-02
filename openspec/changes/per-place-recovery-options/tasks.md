## 1. PostgreSQL plugin-local options type

- [ ] 1.1 Add `PostgresConnectionRecoveryOptions` to `src/RayTree.Plugins.PostgreSQL` (new file under `Outbox/Notification/` or a `Resilience/` folder): copy the six members, per-field init guards, and `Validate()` from the current Core `ConnectionRecoveryOptions`; update the XML doc to reference Postgres LISTEN reconnect only.
- [ ] 1.2 Repoint `NotificationBasedPublisherOptions.ConnectionRecovery` to `PostgresConnectionRecoveryOptions` (property type + `new()` initializer); remove the `using RayTree.Core.Resilience;` if now unused.
- [ ] 1.3 Update `NotificationBasedPublisher` to consume the plugin-local type (variable/parameter types, `Validate()` call site); confirm no other Core-type reference remains.
- [ ] 1.4 Build `dotnet build src/RayTree.Plugins.PostgreSQL -c Release` clean (warnings-as-errors).

## 2. Kafka plugin-local options type

- [ ] 2.1 Add `KafkaConnectionRecoveryOptions` to `src/RayTree.Plugins.Kafka` (new file): same six members, guards, and `Validate()`; XML doc references the Kafka publisher/consumer fatal-error rebuild loops.
- [ ] 2.2 Repoint `KafkaPublisherOptions.ConnectionRecovery` and `KafkaConsumerOptions.ConnectionRecovery` to `KafkaConnectionRecoveryOptions`; remove now-unused `using RayTree.Core.Resilience;`.
- [ ] 2.3 Update `KafkaPublisher` and `KafkaConsumer` to consume the plugin-local type at all use sites (rebuild loop tuning, `Validate()` calls).
- [ ] 2.4 Build `dotnet build src/RayTree.Plugins.Kafka -c Release` clean.

## 3. Remove Core type and Hosting binding

- [ ] 3.1 Delete `src/RayTree.Core/Resilience/ConnectionRecoveryOptions.cs`.
- [ ] 3.2 Delete `src/RayTree.Hosting/ChangeTrackingRecoveryKeys.cs` and remove the two `services.Configure<ConnectionRecoveryOptions>(...)` calls (plus the explanatory comment block) from `ServiceCollectionExtensions.AddChangeTracking`; remove now-unused usings.
- [ ] 3.3 Build `dotnet build RayTree.slnx -c Release` to surface every remaining reference to the removed type/keys across the solution; fix any stragglers.

## 4. Remove connection-recovery metrics from RayTreeMeter

- [ ] 4.1 In `src/RayTree.Core/Telemetry/RayTreeMeter.cs` remove the four `raytree.connection.*` instruments (`ConnectionDisconnects`, `ConnectionRecoveries`, `ConnectionRecoveryDuration`, the `raytree.connection.state` observable gauge), their `CreateCounter`/`CreateHistogram`/`CreateObservableGauge` registrations, the `_connectionStateSources` list, the `_connectionStateGate` lock, the `ObserveConnectionStates` callback, and the nested `ConnectionStateSubscription` type.
- [ ] 4.2 Remove the three public facade methods `RecordConnectionDisconnect`, `RecordConnectionRecovery`, and `RegisterConnectionStateGauge`. Remove the now-orphaned `ComponentTag`/`EndpointTag`/`OutcomeTag` helpers if nothing else uses them.
- [ ] 4.3 Narrow the remaining emit/register methods to `internal`: `RecordPublishSuccess`, `RecordPublishFailure`, `RecordPayloadSize`, `RecordBatchSize`, `RegisterPendingGauge`. Verify `RayTreeMeter`'s public surface is now only `MeterName`, the constructors, `DefaultPendingCacheTtl`, and `Dispose()`.
- [ ] 4.4 Build `dotnet build src/RayTree.Core -c Release`, then `dotnet build src/RayTree.Plugins.PostgreSQL -c Release` to confirm the PostgreSQL plugin (IVT-privileged) still compiles against the internalized members; surface any remaining call sites of the removed connection facade.

## 5. Remove metric emission at all call sites

- [ ] 5.1 `src/RayTree.Core/Distribution/OutboxPublisherService.cs`: delete the `RecordConnectionDisconnect`/`RecordConnectionRecovery` calls in `HandleBatchError`/`EmitOutboxRecovered`; keep the `_outboxUnhealthy` tracking, the `Error→Warning` log demotion, and the "outbox connection recovered" `Information` log.
- [ ] 5.2 `src/RayTree.Plugins.PostgreSQL/Outbox/Notification/NotificationBasedPublisher.cs`: delete connection-metric emission in the LISTEN reconnect and fallback-poll paths; keep the reconnect loop, `_listenerHealthy` handling, per-outbox unhealthy tracking, and all logs.
- [ ] 5.3 `src/RayTree.Plugins.Kafka/KafkaPublisher.cs` and `KafkaConsumer.cs`: delete connection-metric emission in the fatal-error/rebuild paths; keep rebuild behavior and logs.
- [ ] 5.4 `src/RayTree.Plugins.RabbitMQ/RabbitMqPublisher.cs`: remove the `_meter` field, the constructor `RayTreeMeter?` parameter, the `RegisterConnectionStateGauge` subscription, and the two `RecordConnection*` calls; keep the three event handlers, the `_lastShutdownAt` duration tracking, and the `Warning`/`Information` logs.
- [ ] 5.5 `src/RayTree.Plugins.RabbitMQ/RabbitMqConsumer.cs`: remove the `_meter` field, the constructor `RayTreeMeter? meter` parameter, the `_stateGaugeSubscription`, the `ConnectionShutdownAsync`/`RecoverySucceededAsync` subscriptions and their `OnConnectionShutdownAsync`/`OnRecoverySucceededAsync` handlers, and the matching `-=` detach in dispose.
- [ ] 5.6 Update the RabbitMQ builder/subscriber extensions (`RabbitMqBuilderExtensions.cs`, `RabbitMqSubscriberExtensions.cs`) and any Kafka extensions that forwarded a `RayTreeMeter` solely for connection metrics so they no longer pass it to the publisher/consumer constructors.
- [ ] 5.7 Build `dotnet build RayTree.slnx -c Release` clean.

## 6. Tests

- [ ] 6.1 Move the validation/defaults assertions from `tests/RayTree.Core.Tests/Resilience/ConnectionRecoveryOptionsTests.cs` into the PostgreSQL and Kafka test projects, retargeting `PostgresConnectionRecoveryOptions` / `KafkaConnectionRecoveryOptions`; delete the Core test file.
- [ ] 6.2 Delete `tests/RayTree.Core.Tests/Resilience/ConnectionRecoveryConfigurationTests.cs` (host binding removed) and `tests/RayTree.Core.Tests/Resilience/RecoveryMetricsTests.cs` (instruments removed).
- [ ] 6.3 Delete `tests/RayTree.Plugins.RabbitMQ.Tests/RabbitMqRecoveryMetricsTests.cs` (RabbitMQ recovery metrics removed).
- [ ] 6.4 Strip metric assertions from `tests/RayTree.Plugins.Kafka.Tests/KafkaRecoveryMetricsTests.cs`, `tests/RayTree.Core.Tests/Resilience/OutboxPublisherServiceConnectionFaultTests.cs`, and `tests/RayTree.Plugins.PostgreSQL.Tests/NotificationBasedPublisherRecoveryTests.cs`; retarget the plugin-local option types and keep the log/behavior assertions.
- [ ] 6.5 Confirm tests still compile against the internalized emit methods (test projects have `InternalsVisibleTo`); any test that called a now-`internal` method as "public" needs no change beyond the build passing. If a test asserted on the removed public facade, delete that assertion.
- [ ] 6.6 Run `dotnet test tests/RayTree.Core.Tests`, the PostgreSQL/Kafka/RabbitMQ test projects (unit-only filter where Docker is unavailable); all green.

## 7. Docs and changelog

- [ ] 7.1 Update `CLAUDE.md` (Connection recovery section + the `RayTreeMeter` "18 instruments" count and the four shared connection instruments paragraph; note that emit methods are now `internal` and the public surface is construct-and-observe only) and `AGENTS.md`: describe the per-plugin options types, the removed Hosting binding, the removed connection metrics (recovery is log-only), and the narrowed `RayTreeMeter` public surface.
- [ ] 7.2 Update `docs/opentelemetry-metrics.md` (remove the four `raytree.connection.*` rows and bucket guidance), and the plugin READMEs (`src/RayTree.Plugins.RabbitMQ/README.md`, `src/RayTree.Plugins.PostgreSQL/README.md`, `src/RayTree.Plugins.Kafka/README.md`) where they reference the Core type, the bound config sections, or the connection metrics.
- [ ] 7.3 Add a `CHANGELOG.md` entry under a new version: BREAKING — `ConnectionRecoveryOptions` + `ChangeTrackingRecoveryKeys` removed (replaced by `PostgresConnectionRecoveryOptions` / `KafkaConnectionRecoveryOptions`); all `raytree.connection.*` metrics + `RayTreeMeter.RecordConnection*`/`RegisterConnectionStateGauge` removed; RabbitMQ publisher/consumer constructors no longer take `RayTreeMeter`. Include the before/after migration snippet and the metrics-removal observability note.
- [ ] 7.4 Final full `dotnet build RayTree.slnx -c Release` + run the non-Docker unit-test suite; confirm clean.
