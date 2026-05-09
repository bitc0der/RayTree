## 1. Dependencies & Project Files

- [x] 1.1 Add `Microsoft.Extensions.Logging.Abstractions` version `8.0.2` to `Directory.Packages.props`
- [x] 1.2 Add `<PackageReference Include="Microsoft.Extensions.Logging.Abstractions" />` to `src/RayTree.Core/RayTree.Core.csproj`

## 2. OutboxPublisherService — Logger Injection & Logging

- [x] 2.1 Add `ILoggerFactory?` parameter to `OutboxPublisherService` constructor; resolve `ILogger<OutboxPublisherService>` using `(loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<OutboxPublisherService>()`
- [x] 2.2 In `PollAndPublishAsync`, replace the silent `catch (Exception)` with a log call: `_logger.LogError(ex, "Error processing outbox batch for {EntityType}", _entityType.Name)`
- [x] 2.3 In `PublishWithRetryAsync`, log `Warning` on each retry attempt: `_logger.LogWarning(ex, "Publish attempt {Attempt} of {MaxRetries} failed for {EntityType}, retrying", retries, _options.MaxRetryCount, _entityType.Name)`
- [x] 2.4 In `PublishWithRetryAsync`, log `Error` before the final re-throw: `_logger.LogError(ex, "Failed to publish change {ChangeId} for {EntityType} after {Retries} attempts", change.Id, _entityType.Name, retries)`

## 3. ChangePublisher — Logger Factory Threading

- [x] 3.1 Add `ILoggerFactory?` field to `ChangePublisher`; add it as a constructor parameter (or expose a `SetLoggerFactory` internal method called before `InitializeAsync`)
- [x] 3.2 Pass `_loggerFactory` to each `new OutboxPublisherService(this, entityType, Options, _loggerFactory)` call in `InitializeAsync`

## 4. ChangePublisherBuilder — Logger Factory Plumbing

- [x] 4.1 Add an internal `void UseLoggerFactory(ILoggerFactory? factory)` method to `ChangePublisherBuilder` that stores the factory
- [x] 4.2 In `ChangePublisherBuilder.Build()`, pass the stored factory to the `new ChangePublisher()` constructor

## 5. ChangeSubscriber — Logger Injection & Logging

- [x] 5.1 Add `ILogger<ChangeSubscriber>?` parameter to `ChangeSubscriber` constructor; resolve from `NullLoggerFactory` when null
- [x] 5.2 In `ProcessMessageAsync`, after `ResolveType` returns null, log `Warning`: `_logger.LogWarning("Unknown entity type '{EntityType}' in message envelope, skipping", envelope.EntityType)`
- [x] 5.3 In `InvokeWithRetryAsync`, log `Warning` on each retry: `_logger.LogWarning(ex, "Handler for {EntityType} failed on attempt {Attempt}, retrying", registration.EntityType.Name, attempt)`
- [x] 5.4 In `InvokeWithRetryAsync`, when `SkipOnFailure` is true, log `Error` before returning: `_logger.LogError(ex, "Handler for {EntityType} failed after {Attempts} attempts, skipping message", registration.EntityType.Name, attempt + 1)`

## 6. ChangeSubscriberBuilder — Logger Factory Plumbing

- [x] 6.1 Add an internal `void UseLoggerFactory(ILoggerFactory? factory)` method to `ChangeSubscriberBuilder` that stores the factory
- [x] 6.2 In `ChangeSubscriberBuilder.Build(...)`, pass `loggerFactory.CreateLogger<ChangeSubscriber>()` as the new constructor argument

## 7. IChangeTrackingBuilder & ChangeTrackingBuilder — Public API

- [x] 7.1 Add `IChangeTrackingBuilder UseLoggerFactory(ILoggerFactory loggerFactory)` to the `IChangeTrackingBuilder` interface
- [x] 7.2 Implement `UseLoggerFactory` in `ChangeTrackingBuilder`: store the factory and call `_publisherBuilder.UseLoggerFactory(factory)` and `_subscriberBuilder.UseLoggerFactory(factory)` from `BuildInternal()`

## 8. ChangeTrackingHostedService — Logger Injection & Lifecycle Logging

- [x] 8.1 Add `ILogger<ChangeTrackingHostedService>?` parameter to `ChangeTrackingHostedService` constructor; resolve from `NullLoggerFactory` when null
- [x] 8.2 In `StartAsync`, log `Information` for each consumer loop started: `_logger.LogInformation("Starting change tracking consumer loop {Index} of {Total}", i + 1, count)`
- [x] 8.3 In `StopAsync`, log `Information` after cancellation: `_logger.LogInformation("Change tracking hosted service stopped")`

## 9. ServiceCollectionExtensions — Automatic DI Wiring

- [x] 9.1 Change `services.AddSingleton<EntityChangeTracker>(_ => builder.Build())` to a factory lambda that calls `sp.GetService<ILoggerFactory>()` and passes it to `builder.UseLoggerFactory(lf)` before calling `builder.Build()`

## 10. Tests

- [x] 10.1 Add a unit test to `RayTree.Core.Tests` verifying that `ChangeTrackingBuilder.Build()` succeeds without a logger factory (null-safety / NullLogger default path)
- [x] 10.2 Add a unit test verifying that a `Warning` is logged by `OutboxPublisherService` when a publish attempt fails before the final retry
- [x] 10.3 Add a unit test verifying that an `Error` is logged by `ChangeSubscriber` when `SkipOnFailure` drops a message after exhausting retries
