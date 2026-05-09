## Why

RayTree's background services (outbox polling loop, publish retries, subscriber handler retries) silently swallow exceptions today, making production failures invisible. Operators have no way to observe that the library is broken without writing custom wrappers — adding structured logging via `Microsoft.Extensions.Logging.Abstractions` gives them that visibility at zero cost when running in a host without a logger wired up.

## What Changes

- Add `Microsoft.Extensions.Logging.Abstractions` as a dependency to `RayTree.Core`.
- Accept `ILoggerFactory?` as an optional dependency on `ChangeTrackingBuilder` via a new `UseLoggerFactory` method on `IChangeTrackingBuilder`.
- Thread loggers through `ChangePublisher`, `OutboxPublisherService`, and `ChangeSubscriber` — all classes that currently swallow exceptions silently.
- Log errors when outbox-poll or publish retries fail; log warnings on each retry attempt; log an error with `SkipOnFailure` when a handler is permanently dropped.
- Log lifecycle events (consumer start/stop) in `ChangeTrackingHostedService`.
- When running under the .NET Generic Host, resolve `ILoggerFactory` from DI automatically in `ServiceCollectionExtensions` — no user configuration required.

## Capabilities

### New Capabilities

- `structured-logging`: Structured `ILogger`-based observability for all background services — outbox publisher, publish retries, subscriber handler retries, and hosted-service lifecycle. Callers opt in by supplying an `ILoggerFactory`; defaults to `NullLoggerFactory` so existing code requires no changes.

### Modified Capabilities

<!-- No existing capability requirements are changing. -->

## Impact

- **`RayTree.Core`** — new package dependency on `Microsoft.Extensions.Logging.Abstractions 8.x`; changed constructors on `ChangePublisher`, `OutboxPublisherService`, and `ChangeSubscriber` (all additive, existing call-sites remain valid via optional parameters / builder-threaded factory).
- **`RayTree.Hosting`** — `ServiceCollectionExtensions.AddChangeTracking` automatically passes `ILoggerFactory` from the DI container; `ChangeTrackingHostedService` gains lifecycle log messages.
- **`IChangeTrackingBuilder`** — new `UseLoggerFactory(ILoggerFactory)` method (additive, non-breaking).
- **Internal builder chain** (`ChangePublisherBuilder`, `ChangeSubscriberBuilder`) — internal `UseLoggerFactory` plumbing, not visible on public interfaces.
- **No wire-format or schema changes** — purely runtime observability.
