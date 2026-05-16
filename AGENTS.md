# AGENTS.md

Guidance for AI coding agents working in this repository.
See `CLAUDE.md` for deep architecture documentation, plugin contracts, and key design decisions.

## Build & Test

```bash
# Build (Debug)
dotnet build RayTree.slnx

# Build (Release — used by CI)
dotnet build RayTree.slnx -c Release

# Unit tests (no Docker required)
dotnet test tests/RayTree.Core.Tests
dotnet test tests/RayTree.Plugins.InMemory.Tests
dotnet test tests/RayTree.EntityFrameworkCore.Tests
dotnet test tests/RayTree.OpenTelemetry.Tests
dotnet test tests/RayTree.Plugins.Compressors.{Brotli,Gzip,Lz4}.Tests
dotnet test tests/RayTree.Plugins.Serializers.{Json,MessagePack,Protobuf}.Tests

# Single test by name
dotnet test tests/RayTree.Core.Tests --filter "FullyQualifiedName~<TestName>"

# Integration tests (Docker required)
dotnet test tests/RayTree.Plugins.PostgreSQL.Tests
dotnet test tests/RayTree.Plugins.RabbitMQ.Tests
dotnet test tests/RayTree.Plugins.Kafka.Tests
```

**Always build and run the relevant unit tests before considering a task complete.**
Integration tests are only required when changing PostgreSQL, Kafka, or RabbitMQ plugin code.

## Hard Constraints

- `TreatWarningsAsErrors=true` is global — zero warnings are acceptable.
- Nullable warnings are errors. Every reference type must be correctly annotated (`?` or non-nullable).
- New packages must be added to `Directory.Packages.props` with a version; `.csproj` references omit the version attribute.
- Do not modify `Directory.Packages.props` versions unless explicitly asked.

## Coding Rules

### Naming and style

- Follow `.editorconfig` — it wins over any general suggestion.
- Private/internal fields: `_camelCase`. Static private/internal fields: `s_PascalCase`. Constants: `PascalCase`.
- Expression-bodied members for single-expression methods, properties, and accessors.
- `using` directives outside the namespace; System namespaces first.
- Braces always on a new line (`csharp_new_line_before_open_brace = all`).

### async / await

- All I/O methods must be `async` and accept `CancellationToken` as the last parameter. Never ignore it.
- Never use `async void`. Use `async Task`. Exception: framework-imposed event handlers.
- Never use `.Result` or `.Wait()` — always `await`.
- Do not add `ConfigureAwait(false)` — omit it everywhere for consistency.
- Async methods carry the `Async` suffix.

### Exception handling

- Catch the most specific exception type available.
- Never `catch (Exception)` except at a top-level loop boundary; always log at `Error` with the original exception before continuing or rethrowing.
- Never swallow exceptions silently.
- Let `OperationCanceledException` propagate — do not catch it in inner loops.
- `InvalidOperationException` → programmer error (wrong call order, missing config).
- `ArgumentException` / `ArgumentNullException` → bad caller input.
- Prefer `bool`-returning `Try*` methods over `try/catch` for control flow.

### Disposable

- Use `IAsyncDisposable` for types that own async resources (channels, connections, background tasks).
- Always `await using` / `using` at the call site; never call `Dispose()` / `DisposeAsync()` manually.
- Cancel the `CancellationTokenSource` before `Dispose()` on background-loop owners.

### LINQ and collections

- Never enumerate an `IEnumerable<T>` more than once — materialise with `.ToList()` / `.ToArray()` first.
- Public APIs return `IReadOnlyList<T>` or `IReadOnlyCollection<T>` when callers must not mutate; `IEnumerable<T>` only for lazy streaming.
- LINQ is for projections, not mutations. Use `foreach` for side-effects.
- No more than three chained LINQ operators without an intermediate named variable.

### Span&lt;T&gt; and Memory&lt;T&gt;

- Use `Span<T>` / `ReadOnlySpan<T>` for synchronous, stack-local slicing. Never store a `Span<T>` in a field or closure.
- Use `Memory<T>` / `ReadOnlyMemory<T>` when the slice crosses an `await` or must be heap-stored.
- Never allocate a new `byte[]` to pass a sub-range — use `.Slice()`, `AsSpan()`, or `AsMemory()`.
- Prefer `Span<T>` overloads of `BinaryPrimitives`, `MemoryMarshal`, and `Encoding` over array-allocating variants.
- Pick one ownership model (`Span<T>` or `Memory<T>`) per logical operation and stay consistent.

### Strings and primitives

- Use `string.Empty` instead of `""` for variables. Inline literals in interpolations are fine.
- Prefer `$"..."` interpolation over `+` or `string.Concat`.
- Do not call `.ToString()` directly on a nullable — use `?.ToString() ?? string.Empty`.

### Logging

- `NullLoggerFactory.Instance` / `NullLogger<T>.Instance` defaults belong **only** in builders and builder-extension methods.
- All runtime service classes require a non-nullable `ILoggerFactory` / `ILogger<T>` constructor parameter — no internal fallback.

## Design Principles (summary)

| Principle | Rule |
|---|---|
| SRP | One reason to change per class. Publisher, subscriber, and tracker are separate. |
| OCP | Extend via new plugin implementations (`IOutbox`, `IQueuePublisher`, etc.), never by editing core. |
| LSP | Plugin implementations must be fully substitutable. |
| ISP | Keep interfaces narrow. `IQueuePublisher` and `IQueueConsumer` are separate. |
| DIP | Core depends on abstractions, never on concrete plugin types. |
| KISS | Simplest solution that works. No speculative abstractions or unused config knobs. |
| DRY | Shared logic lives in one place. No duplication across publisher and subscriber. |
| YAGNI | Do not add features for hypothetical future needs. |
| Constructor injection | All dependencies declared in the constructor. No property setters or post-construction wiring. |
| Dead code | Remove unused fields, parameters, methods, and classes immediately. |

## Testing Conventions

- Method names: `MethodUnderTest_Scenario_ExpectedBehaviour`
  (e.g., `WriteAsync_WhenEntityIsNull_ThrowsArgumentNullException`)
- Structure: Arrange / Act / Assert, separated by blank lines.
- One logical outcome per test; multiple `Assert` calls are fine for the same fact.
- No shared mutable state between tests in the same class.
- Unit tests must not touch the file system, network, or `DateTime.UtcNow`. Use `TimeProvider` or a clock abstraction.
- Integration tests that share a container: mark `[NonParallelizable]` and use unique topic/queue names per test.
- Test edge cases: null inputs, empty collections, cancellation, exceptions, boundary values.
- Use `Assert.ThrowsAsync<T>()` for async exception tests, not `try/catch` with `Assert.Fail()`.
- Mock at the interface level. Use plugin interfaces (`IOutbox`, `IQueuePublisher`, etc.) as mock boundaries.

## Nullability Discipline

- Every reference type must be annotated. `string` means non-null; `string?` means nullable.
- Initialize required fields in the constructor or with a default value.
- Use `ArgumentNullException.ThrowIfNull()` for guard clauses.
- Return `Empty` collections instead of `null`. Use `Array.Empty<T>()`, `Enumerable.Empty<T>()`, or `ReadOnlyCollection<T>.Empty`.
- When in doubt, make it non-nullable and throw `ArgumentNullException` if the caller passes null.

## .NET Conventions

- Prefer interfaces over abstract classes for plugin contracts.
- Constructor injection for all dependencies. No service locator, no `static` state, no hidden dependencies.
- Avoid `object` parameters in public APIs where possible — use generics or specific types.
- Document public APIs with XML doc comments (`/// <summary>`) — especially parameters, return values, and exceptions thrown.
- Return `IAsyncEnumerable<T>` for streaming results. Use `IEnumerable<T>` only when lazy streaming is intentional.

## AI Coding Agent Rules

### Efficient Workflow

- Don't guess, ask clarifying questions — especially when in doubt.
- Read before editing. Always read the full file (or relevant section) before making changes.
- Search before creating. Check if a type, method, or pattern already exists before writing new code.
- Batch independent operations. Identify all targets first, then edit them in parallel.
- Use existing patterns. Copy the structure, naming, and conventions from neighboring files.
- Understand the dependency graph before changing interfaces.

### Safety First

- Never commit unless the user explicitly asks.
- Never delete files unless they are provably dead code (zero references across the entire solution).
- Never hardcode secrets, connection strings, or credentials. Use configuration, environment variables, or test fixtures.
- Never suppress warnings with `#pragma` or `<NoWarn>` unless there is a documented, unavoidable reason.

### Verify Before Declaring Done

- Always build after making code changes: `dotnet build RayTree.slnx`
- Always run relevant tests after changes.
- Zero warnings is the standard — `TreatWarningsAsErrors=true` is global.
- If tests fail, fix the root cause. Do not modify tests unless the test itself was wrong.
- Run `dotnet format --verify-no-changes` if unsure about style compliance.

## CI

`.github/workflows/ci.yml` has three job groups: `build` (compile gate, uploads compiled output as an artifact with 1-day retention), `unit-tests` (9-way parallel matrix, no Docker, downloads build artifact — no rebuild), `integration-tests` (3-way matrix: PostgreSQL / RabbitMQ / Kafka, also downloads build artifact). No job rebuilds the solution; all test jobs depend on the shared artifact from `build`.

## Repository Layout

```
src/
  RayTree.Core/                        # EntityChangeTracker, builder, publisher, subscriber
  RayTree.Hosting/                     # IHostedService + AddChangeTracking DI extension
  RayTree.EntityFrameworkCore/         # SaveChangesAsync interceptor
  RayTree.OpenTelemetry/               # OTel SDK wiring (peer assembly)
  RayTree.Plugins.PostgreSQL/          # Outbox + Repository + NOTIFY/LISTEN publisher
  RayTree.Plugins.InMemory/            # In-process queue (tests / local dev)
  RayTree.Plugins.Kafka/               # KafkaPublisher + KafkaConsumer
  RayTree.Plugins.RabbitMQ/            # RabbitMqPublisher + RabbitMqConsumer
  RayTree.Plugins.Serializers.*/       # JSON, MessagePack, Protobuf
  RayTree.Plugins.Compressors.*/       # Gzip, Brotli, LZ4
tests/
  RayTree.Core.Tests/                  # Unit tests (no Docker)
  RayTree.Plugins.InMemory.Tests/      # Unit tests (no Docker)
  RayTree.EntityFrameworkCore.Tests/   # Unit tests (no Docker)
  RayTree.OpenTelemetry.Tests/         # Unit tests (no Docker)
  RayTree.Plugins.Serializers.*.Tests/ # Unit tests (no Docker)
  RayTree.Plugins.Compressors.*.Tests/ # Unit tests (no Docker)
  RayTree.Plugins.PostgreSQL.Tests/    # Integration tests (Docker)
  RayTree.Plugins.RabbitMQ.Tests/      # Integration tests (Docker)
  RayTree.Plugins.Kafka.Tests/         # Integration tests (Docker)
```
