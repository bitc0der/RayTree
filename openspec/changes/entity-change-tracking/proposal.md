## Why

Applications need reliable, decoupled mechanisms to track entity changes and distribute them to downstream consumers. Current approaches are tightly coupled to specific databases, queues, or frameworks, making reuse and swapping components difficult. This change introduces a modular, plugin-based entity change tracking system with outbox pattern support, designed for .NET applications with EF Core integration.

## What Changes

- Introduce a core entity change tracking library with plugin architecture
- Add EF Core integration for automatic change detection via SaveChanges interception
- Implement outbox pattern with configurable storage per entity (source + outbox tables)
- Support standalone configuration and .NET Generic Host integration
- Add plugin system for repository, outbox, queue, serialization, and compression providers
- Include built-in plugins for PostgreSQL, RabbitMQ, Kafka, JSON, and gzip

## Capabilities

### New Capabilities

- `change-tracking-core`: Core abstractions for entity change detection, configuration, and lifecycle management
- `outbox-pattern`: Outbox storage and management with per-entity source/outbox table pairs
- `change-distribution`: Queue-based distribution of entity changes via pub/sub plugins
- `ef-core-integration`: EF Core interceptor for automatic change tracking on SaveChanges
- `plugin-system`: Extensible plugin architecture for repository, outbox, and queue providers
- `serializer-plugins`: Separate assembly with pluggable serialization providers (JSON, Protobuf, MessagePack, etc.)
- `compressor-plugins`: Separate assembly with pluggable compression providers (Gzip, Brotli, LZ4, None, etc.)
- `dotnet-host-integration`: Microsoft.Extensions.DependencyInjection and IHostedService integration
- `standalone-configuration`: Fluent configuration API for use without DI container
- `subscriber-configuration`: Fluent configuration API for consuming entity changes with per-entity handlers, deduplication, and error policies
- `in-memory-plugins`: In-memory implementations of repository, outbox, and queue for testing, development, and single-process scenarios

### Modified Capabilities

<!-- No existing specs to modify -->

## Impact

- New packages/assemblies: core library, EF Core integration, host integration, data plugins (PostgreSQL, RabbitMQ, Kafka), in-memory plugins (repository, outbox, queue), serializer plugins (JSON, Protobuf, MessagePack), compressor plugins (Gzip, Brotli, LZ4)
- Dependencies: Npgsql (PostgreSQL), RabbitMQ.Client, Confluent.Kafka, Microsoft.EntityFrameworkCore, protobuf-net, MessagePack-CSharp, lz4net
- Database schema changes: requires source + outbox tables per tracked entity (unless using in-memory storage), potentially DB triggers
- Existing applications can integrate via NuGet packages or source reference
- Consumers reference only the plugin assemblies they need — serializers/compressors are separate from data plugins
- In-memory plugins have zero external dependencies
