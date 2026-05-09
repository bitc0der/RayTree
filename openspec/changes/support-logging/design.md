## Context

RayTree is a .NET 8 outbox-pattern change-tracking library. Its background services — `OutboxPublisherService` (polling loop + publish retries) and `ChangeSubscriber` (handler retry loop) — currently swallow exceptions silently. `ChangeTrackingHostedService` starts and stops consumer tasks without any lifecycle tracing. The library has no dependency on `Microsoft.Extensions.Logging` today.

The library is used both standalone (via `ChangeTrackingBuilder`) and under the .NET Generic Host (via `ServiceCollectionExtensions.AddChangeTracking`). Any logging solution must work correctly in both modes, including the common "no logger configured" case where all logging should be a no-op.

## Goals / Non-Goals

**Goals:**
- Emit `ILogger`-based structured log events at the right severity from `OutboxPublisherService`, `ChangeSubscriber`, and `ChangeTrackingHostedService`.
- Work transparently under the .NET Generic Host — `ILoggerFactory` is resolved from DI automatically, no user action required.
- Default to `NullLoggerFactory` when no factory is supplied — zero behaviour change for existing standalone users.
- Keep the public API surface addition minimal: one new method on `IChangeTrackingBuilder`.

**Non-Goals:**
- Log-level configuration, custom formatters, or log filtering rules — these are the host's responsibility.
- Logging inside plugin implementations (serializers, compressors, outbox/queue adapters).
- Tracing / OpenTelemetry spans.
- Changing any wire format or persistence schema.

## Decisions

### D1 — Use `Microsoft.Extensions.Logging.Abstractions`, not a concrete provider

`Microsoft.Extensions.Logging.Abstractions` provides `ILogger<T>`, `ILoggerFactory`, and `NullLoggerFactory` / `NullLogger<T>`. Depending only on the abstractions package keeps `RayTree.Core` decoupled from any specific logging backend (Serilog, NLog, console, etc.) and is standard practice for .NET libraries.

*Alternative considered:* shipping no logging dependency and requiring callers to wrap errors in their own handlers. Rejected — it requires every user to write boilerplate and leaves background service failures invisible by default.

### D2 — `ILoggerFactory?` enters through `ChangeTrackingBuilder.UseLoggerFactory`, not individual class constructors

`ChangePublisher` creates `OutboxPublisherService` instances dynamically during `InitializeAsync`. Passing `ILogger<OutboxPublisherService>` directly to each service requires `ChangePublisher` to know the factory anyway. Threading `ILoggerFactory?` through the builder chain (`ChangeTrackingBuilder` → `ChangePublisherBuilder` → `ChangePublisher` → `OutboxPublisherService`) keeps construction deterministic and consistent with how other cross-cutting config (e.g., `OutboxPublisherOptions`) flows through the same path.

`ChangeSubscriber` takes `ILogger<ChangeSubscriber>?` directly in its constructor (created inside `ChangeSubscriberBuilder.Build()`), consistent with how `IDeduplicationStore` and `SubscriberOptions` are injected today.

*Alternative considered:* ambient `ILoggerFactory` via a static property. Rejected — violates DIP and makes tests harder to isolate.

### D3 — `ILoggerFactory?` is optional; default is `NullLoggerFactory.Instance`

Each class that receives a logger resolves it as:
```csharp
private readonly ILogger<T> _logger;

// in constructor:
_logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<T>();
```
This avoids nullable `ILogger<T>?` null-checks at every call site and is the canonical pattern for .NET library authors.

### D4 — `ServiceCollectionExtensions` resolves `ILoggerFactory` from DI at singleton-build time

The existing registration uses a factory lambda `sp => builder.Build()`. Changing it to resolve `ILoggerFactory` before calling `Build()` requires no structural change:

```csharp
services.AddSingleton<EntityChangeTracker>(sp =>
{
    if (sp.GetService<ILoggerFactory>() is { } lf)
        builder.UseLoggerFactory(lf);
    return builder.Build();
});
```

`ILoggerFactory` is always registered when `AddLogging()` is called (which `AddHostedService` and most host configurations do automatically). Using `GetService` (not `GetRequiredService`) keeps it safe for minimal setups that omit logging.

### D5 — Log levels and event semantics

| Location | Event | Level |
|---|---|---|
| `OutboxPublisherService.PollAndPublishAsync` | Exception escapes the poll batch | `Error` |
| `OutboxPublisherService.PublishWithRetryAsync` | Retry attempt N of MaxRetryCount | `Warning` |
| `OutboxPublisherService.PublishWithRetryAsync` | All retries exhausted (before re-throw) | `Error` |
| `ChangeSubscriber.InvokeWithRetryAsync` | Handler retry attempt | `Warning` |
| `ChangeSubscriber.InvokeWithRetryAsync` | Handler permanently dropped (`SkipOnFailure`) | `Error` |
| `ChangeSubscriber.ProcessMessageAsync` | Unknown entity type in envelope | `Warning` |
| `ChangeTrackingHostedService.StartAsync` | Consumer loop started per queue | `Information` |
| `ChangeTrackingHostedService.StopAsync` | Graceful shutdown complete | `Information` |

All log messages use structured parameters (e.g., `{EntityType}`, `{Attempt}`, `{MaxRetries}`) — no string interpolation.

## Risks / Trade-offs

**Constructor signature changes on `ChangePublisher`, `OutboxPublisherService`, `ChangeSubscriber`** → Mitigation: all new parameters are optional (nullable with a null-defaults-to-NullLogger pattern), so existing call-sites in tests and user code compile without change. The only public API addition is `IChangeTrackingBuilder.UseLoggerFactory`.

**`ChangePublisherBuilder` and `ChangeSubscriberBuilder` gain internal `UseLoggerFactory` plumbing not exposed on their interfaces** → Mitigation: these are internal builder details; `IChangePublisherBuilder` and `IChangeSubscriberBuilder` stay unchanged, keeping the ISP principle intact.

**`ServiceCollectionExtensions` mutates `builder` inside the DI factory lambda** → The `builder` is captured in a closure and already frozen after `configure?.Invoke(builder)`. `UseLoggerFactory` is called once before `Build()`, which is safe because `ThrowIfBuilt()` guards prevent post-build mutation, and `UseLoggerFactory` is called before `Build()`.

## Migration Plan

- All changes are additive and backward-compatible at the binary level.
- No database schema changes, no message envelope changes.
- Existing users see no behaviour change until they call `UseLoggerFactory(...)` or run under a host with `ILoggerFactory` registered.
- No rollback steps required; reverting is a simple package downgrade.

## Open Questions

None — all decisions have clear answers given the constraints above.
