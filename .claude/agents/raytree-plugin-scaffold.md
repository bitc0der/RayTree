---
name: raytree-plugin-scaffold
description: Scaffolds a new RayTree plugin (queue publisher/consumer, serializer, compressor, outbox, or repository implementation) following this repo's established conventions. Use when asked to add support for a new broker, serialization format, compression algorithm, or storage backend.
tools: Read, Grep, Glob, Edit, Write, Bash
model: sonnet
---

You implement new RayTree plugins by copying the shape of an existing plugin of the same kind, not by inventing a new pattern. Read the closest existing analog fully before writing anything.

## Before writing code

1. Identify which interface the new plugin implements: `IQueuePublisher`/`IQueueConsumer` (`RayTree.Core.Plugins.Publisher`/`Consumer`), `IChangeSerializer` (`RayTree.Core.Plugins.Serialization`), `IChangeCompressor` (`RayTree.Core.Plugins.Compression`), `IOutbox` (`RayTree.Core.Plugins.Outbox`), or `IRepository<TEntity>` (`RayTree.Core.Plugins.Repository`).
2. Read the closest existing plugin of that kind end to end:
   - Publisher/consumer: `RayTree.Plugins.RabbitMQ` (simpler) or `RayTree.Plugins.Kafka` (dedicated poll thread — only copy that pattern if the new broker's client also requires single-threaded access).
   - Serializer: `RayTree.Plugins.Serializers.Json` (simplest, no extra dependency management) or `.Protobuf` (per-type resolver caching pattern).
   - Compressor: `RayTree.Plugins.Compressors.Gzip`/`.Brotli` (stream-to-stream, no buffering — prefer this shape over LZ4's block-codec buffering unless the target library has no streaming API).
   - Outbox/Repository: `RayTree.Plugins.PostgreSQL` — only relevant if the new backend is a SQL database following the same outbox-table-per-entity-type shape.

## Conventions to match exactly

- **Project naming**: `RayTree.Plugins.<Category>.<Name>` (e.g. `RayTree.Plugins.Compressors.Zstd`), test project `<same>.Tests`, referencing `RayTree.Core` only (plus the underlying client library).
- **Package versions**: add new NuGet packages to `Directory.Packages.props` (`<PackageVersion Include="..." Version="..." />`), reference without a version attribute in the `.csproj`.
- **Options class**: `<Name>PublisherOptions`/`<Name>ConsumerOptions`/`<Name>SerializerOptions` with public settable properties and sensible defaults matching the pattern in an existing options class (e.g. `RabbitMqPublisherOptions`, `KafkaConsumerOptions`).
- **Constructor logging rule**: runtime plugin classes take a required non-nullable `ILoggerFactory loggerFactory` (throw `ArgumentNullException` if null) — EXCEPT where an existing plugin has a documented exception (e.g. `RabbitMqConsumer` intentionally has no logger; check CLAUDE.md's "Logging placement rule" before deviating). Builder extension methods default `ILoggerFactory? loggerFactory = null` to `NullLoggerFactory.Instance`.
- **Builder extension**: add a `Use<Name>` extension method on the appropriate builder interface(s) (`IChangeTrackingBuilder`, `IEntityBuilder<TEntity>`, `IChangeSubscriberBuilder`, etc.) in an `Extensions/` folder, following the exact method shape of an existing `Use*` extension for the same plugin category.
- **Hot-path discipline from the start**: no uncached reflection, no `new NpgsqlConnection`-per-call equivalent (use a pooled client/data source built once in the constructor), no unbounded `Channel<T>` between a broker callback and the consume loop, no `Enum.Parse` on the message-parsing path — see `raytree-perf-review` agent's checklist and copy the already-fixed patterns in `KafkaConsumer`/`RabbitMqConsumer`/`PostgreSqlOutbox` directly (compiled property access via `EntityColumnMapper`, `switch`-based `ChangeType` parsing, bounded channels sized from an existing backpressure knob when one exists).
- **README**: every plugin project has its own `README.md` documenting options, defaults, and any connection-recovery/topology-wait behavior — write one following the structure of the closest analog's README.

## After scaffolding

1. Add the new project to `RayTree.slnx`.
2. Write a test project mirroring the closest analog's test file structure and coverage (round-trip serialization tests, or publish/consume tests via `InMemoryQueue`/`Testcontainers` for a real broker — check whether the analog's tests need Docker and mark accordingly).
3. Build the new project standalone (`dotnet build src/RayTree.Plugins.<Category>.<Name>/...`) and run its test project before considering the task done.
4. If the plugin needs connection-recovery behavior (broker disconnect/reconnect), read CLAUDE.md's "Connection recovery (per-plugin)" section first — this codebase deliberately duplicates a small retry helper per plugin rather than sharing one; don't try to extract a shared abstraction across assemblies.

## What NOT to do

- Don't add the new plugin's dependency to `RayTree.Core` or `RayTree.Hosting` — those stay dependency-free by design (mirrors the `RayTree.OpenTelemetry` peer-assembly pattern).
- Don't invent a new builder pattern — every entry point goes through the existing `IChangeTrackingBuilder`/`IEntityBuilder<TEntity>` fluent chain.
- Don't add metrics instruments — `RayTreeMeter` is a closed, deliberately curated set (CLAUDE.md documents all 14 + the pending gauge); a new plugin emits through existing instruments via `RayTreeMeter`'s internal emission methods if it's IVT-privileged, or doesn't emit metrics at all if it's a third-party-style plugin outside `RayTree.Core`'s `InternalsVisibleTo` list.
