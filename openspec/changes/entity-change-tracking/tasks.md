## 1. Project Setup

- [ ] 1.1 Create `Directory.Build.props` with common MSBuild settings (target framework, nullable, implicit usings, warning level, treat warnings as errors)
- [ ] 1.2 Create `Directory.Packages.props` with centralized package version management (CentralPackageManagement enabled)
- [ ] 1.3 Create solution structure with projects: RayTree.Core, RayTree.EntityFrameworkCore, RayTree.Hosting, RayTree.Subscriber, RayTree.Plugins.PostgreSQL, RayTree.Plugins.RabbitMQ, RayTree.Plugins.Kafka, RayTree.Plugins.InMemory, RayTree.Plugins.Serializers.Json, RayTree.Plugins.Serializers.Protobuf, RayTree.Plugins.Serializers.MessagePack, RayTree.Plugins.Compressors.Gzip, RayTree.Plugins.Compressors.Brotli, RayTree.Plugins.Compressors.Lz4
- [ ] 1.4 Define all package versions in `Directory.Packages.props` (Microsoft.EntityFrameworkCore, Npgsql, RabbitMQ.Client, Confluent.Kafka, protobuf-net, MessagePack, lz4net, System.Text.Json, System.IO.Compression)
- [ ] 1.5 Set up project references and solution file
- [ ] 1.6 Configure per-project .csproj files with package references using `VersionOverride` only when needed, otherwise inheriting from central management
- [ ] 1.7 Add NUnit test packages to `Directory.Packages.props` (NUnit, NUnit.Analyzers, NUnit3TestAdapter, Microsoft.NET.Test.Sdk)
- [ ] 1.8 Configure test project templates with NUnit + Moq + Testcontainers

## 2. Core Abstractions

- [ ] 2.1 Define `IEntityChangeTracker` interface with change detection methods
- [ ] 2.2 Define `IRepository` interface for entity persistence operations
- [ ] 2.3 Define `IOutbox` interface for change storage operations
- [ ] 2.4 Define `IQueuePublisher` interface for message publishing
- [ ] 2.5 Define `IChangeSerializer` interface for message serialization
- [ ] 2.6 Define `IChangeCompressor` interface for message compression
- [ ] 2.7 Define `EntityChange` model with metadata fields (change_type, timestamp, version, correlation_id, entity_type)
- [ ] 2.8 Define `ChangeType` enum (Insert, Update, Delete)
- [ ] 2.9 Define `EntityConfiguration` model for per-entity plugin settings

## 3. Core Implementation

- [ ] 3.1 Implement `EntityChangeTracker` as the core change detection engine
- [ ] 3.2 Implement thread-safe change capture with concurrent collections
- [ ] 3.3 Implement correlation ID generation and propagation across batched changes
- [ ] 3.4 Implement serialization/compression pipeline (serialize → compress → publish)
- [ ] 3.5 Implement outbox query interface with filtering by published status, entity type, change type, and date range
- [ ] 3.6 Implement outbox cleanup service with configurable retention period

## 4. EF Core Integration

- [ ] 4.1 Implement `EntityChangeInterceptor` implementing `ISaveChangesInterceptor`
- [ ] 4.2 Implement `SavingChanges` detection for Added, Modified, Deleted entities
- [ ] 4.3 Implement `SavedChanges` outbox write in same transaction
- [ ] 4.4 Implement async interceptor methods for `SaveChangesAsync`
- [ ] 4.5 Implement entity type registration with filter support
- [ ] 4.6 Implement `AddChangeTracking()` extension method on `IServiceCollection`
- [ ] 4.7 Implement automatic interceptor attachment to registered DbContexts
- [ ] 4.8 Implement opt-out mechanism for specific DbContexts
- [ ] 4.9 Implement multi-DbContext support with independent configuration

## 5. Outbox Pattern Implementation

- [ ] 5.1 Implement outbox schema generator for per-entity source and outbox tables
- [ ] 5.2 Implement outbox table model with entity columns + metadata columns
- [ ] 5.3 Implement atomic outbox write within EF Core transaction
- [ ] 5.4 Implement rollback handling for outbox write failures
- [ ] 5.5 Implement outbox polling with configurable interval and batch size

## 6. Change Distribution

- [ ] 6.1 Implement `OutboxPublisherService` with polling loop
- [ ] 6.2 Implement configurable polling interval and batch size
- [ ] 6.3 Implement post-publish confirmation (mark outbox entry as published)
- [ ] 6.4 Implement failed publish retry logic (leave unpublished for next poll)
- [ ] 6.5 Implement graceful shutdown with in-flight operation completion
- [ ] 6.6 Implement `NotificationBasedPublisher` with PostgreSQL LISTEN loop
- [ ] 6.7 Implement `pg_notify` trigger generation for PostgreSQL outbox tables
- [ ] 6.8 Implement notification payload parsing (entity type, outbox row ID, change type)
- [ ] 6.9 Implement fallback polling that activates on LISTEN connection loss
- [ ] 6.10 Implement automatic LISTEN reconnection with backlog scan on reconnect
- [ ] 6.11 Implement `.UseNotificationChannel()` and `.WithFallbackPolling()` fluent API on PostgreSQL outbox config
- [ ] 6.12 Implement multi-channel LISTEN support with notification routing
- [ ] 6.13 Implement notification trigger DDL migration scripts (create/drop)

## 7. Plugin System

- [ ] 7.1 Implement plugin registration via `IChangeTrackingBuilder`
- [ ] 7.2 Implement plugin interface validation at registration time
- [ ] 7.3 Implement global plugin defaults and per-entity plugin overrides
- [ ] 7.4 Implement `IChangeTrackingBuilder` fluent API (UseRepository, UseOutbox, UseQueue, UseSerializer, UseCompressor)

## 8. Built-in Data Plugins

- [ ] 8.1 Implement PostgreSQL repository plugin using Npgsql
- [ ] 8.2 Implement PostgreSQL outbox plugin with table-per-entity schema
- [ ] 8.3 Implement RabbitMQ queue publisher plugin using RabbitMQ.Client
- [ ] 8.4 Implement Kafka queue publisher plugin using Confluent.Kafka

## 9. Serializer Plugin Assemblies

- [ ] 9.1 Create `RayTree.Plugins.Serializers.Json` project with only `RayTree.Core` dependency
- [ ] 9.2 Implement JSON serializer using System.Text.Json
- [ ] 9.3 Implement `.UseJsonSerializer()` extension method
- [ ] 9.4 Create `RayTree.Plugins.Serializers.Protobuf` project with only `RayTree.Core` + protobuf-net dependency
- [ ] 9.5 Implement Protobuf serializer using protobuf-net
- [ ] 9.6 Implement `.UseProtobufSerializer()` extension method
- [ ] 9.7 Create `RayTree.Plugins.Serializers.MessagePack` project with only `RayTree.Core` + MessagePack-CSharp dependency
- [ ] 9.8 Implement MessagePack serializer using MessagePack-CSharp
- [ ] 9.9 Implement `.UseMessagePackSerializer()` extension method

## 10. Compressor Plugin Assemblies

- [ ] 10.1 Create `RayTree.Plugins.Compressors.Gzip` project with only `RayTree.Core` dependency
- [ ] 10.2 Implement Gzip compressor using System.IO.Compression
- [ ] 10.3 Implement `.UseGzipCompressor()` extension method
- [ ] 10.4 Create `RayTree.Plugins.Compressors.Brotli` project with only `RayTree.Core` dependency
- [ ] 10.5 Implement Brotli compressor using System.IO.Compression
- [ ] 10.6 Implement `.UseBrotliCompressor()` extension method
- [ ] 10.7 Create `RayTree.Plugins.Compressors.Lz4` project with only `RayTree.Core` + lz4net dependency
- [ ] 10.8 Implement LZ4 compressor using lz4net
- [ ] 10.9 Implement `.UseLz4Compressor()` extension method
- [ ] 10.10 Implement NoOp compressor in Core (pass-through)
- [ ] 10.11 Implement `.UseNoOpCompressor()` extension method

## 10.5 In-Memory Plugins Assembly

- [ ] 10.5.1 Create `RayTree.Plugins.InMemory` project with only `RayTree.Core` dependency
- [ ] 10.5.2 Implement `InMemoryRepository` using `ConcurrentDictionary<TKey, TEntity>`
- [ ] 10.5.3 Implement `InMemoryOutbox` using `ConcurrentBag<EntityChange>` with thread-safe query and cleanup
- [ ] 10.5.4 Implement `InMemoryQueue` using `Channel<T>` with per-entity-type broadcast
- [ ] 10.5.5 Implement `.UseInMemoryRepository()`, `.UseInMemoryOutbox()`, `.UseInMemoryQueue()` fluent API methods
- [ ] 10.5.6 Implement mixed configuration support (e.g., in-memory repo + external queue)
- [ ] 10.5.7 Implement `.ConsumeFromInMemory()` and `.Subscribe<T>()` for subscriber side
- [ ] 10.5.8 Implement in-memory deduplication store for subscriber
- [ ] 10.5.9 Implement subscription handle with `.Unsubscribe()` support
- [ ] 10.5.10 Implement transaction simulation for in-memory outbox rollback

## 11. .NET Host Integration

- [ ] 11.1 Implement `OutboxPublisherHostedService` implementing `IHostedService`
- [ ] 11.2 Implement hosted service start/stop lifecycle
- [ ] 11.3 Implement `IOptions<ChangeTrackingOptions>` configuration binding
- [ ] 11.4 Implement `IServiceCollection.AddChangeTracking()` with builder pattern
- [ ] 11.5 Implement configuration support via appsettings.json and environment variables

## 12. Standalone Configuration

- [ ] 12.1 Implement `ChangeTrackingConfiguration` builder class
- [ ] 12.2 Implement fluent configuration methods (UseRepository, UseOutbox, UseQueue, UseSerializer, UseCompressor)
- [ ] 12.3 Implement `Build()` method returning `IEntityChangeTracker`
- [ ] 12.4 Implement `StartPublisher()` and `StopPublisher()` for standalone publisher
- [ ] 12.5 Implement `Dispose()` for resource cleanup

## 13. Database Triggers (Optional)

- [ ] 13.1 Implement PostgreSQL trigger generator for source tables
- [ ] 13.2 Implement trigger-based outbox write for non-EF Core changes
- [ ] 13.3 Implement trigger polling mode for outbox publisher
- [ ] 13.4 Document trigger installation and configuration steps

## 14. Subscriber Configuration

- [ ] 14.1 Create `ChangeSubscriberConfiguration` builder class
- [ ] 14.2 Implement `ConsumeEntity<T>()` method with per-entity source configuration
- [ ] 14.3 Implement `FromKafka()`, `FromRabbitMq()`, and `FromInMemory()` entity-level consume source methods
- [ ] 14.4 Implement per-entity serializer/compressor resolution matching publisher config
- [ ] 14.5 Implement `OnChange<T>()` handler registration with optional ChangeType filter
- [ ] 14.6 Implement handler invocation pipeline (decompress → deserialize → route to handlers)
- [ ] 14.7 Implement deduplication store interface (`IDeduplicationStore`)
- [ ] 14.8 Implement Redis deduplication store
- [ ] 14.9 Implement per-entity error handling policies (retry, dead-letter, skip)
- [ ] 14.10 Implement `ChangeSubscriberHostedService` for DI integration
- [ ] 14.11 Implement `IServiceCollection.AddChangeSubscriber()` extension method
- [ ] 14.12 Implement standalone subscriber `IChangeSubscriber` with `StartAsync()`/`StopAsync()`
- [ ] 14.13 Implement multi-entity consume loop with parallel processing

## 15. Testing (NUnit)

- [ ] 15.1 Add unit tests for core abstractions and EntityChangeTracker
- [ ] 15.2 Add unit tests for serialization/compression pipeline
- [ ] 15.3 Add unit tests for EF Core interceptor with in-memory provider
- [ ] 15.4 Add integration tests for PostgreSQL repository and outbox plugins
- [ ] 15.5 Add integration tests for RabbitMQ publisher plugin
- [ ] 15.6 Add integration tests for Kafka publisher plugin
- [ ] 15.6.1 Add integration tests for NOTIFY-based publishing with PostgreSQL
- [ ] 15.6.2 Add integration tests for LISTEN reconnection and backlog scan
- [ ] 15.6.3 Add integration tests for fallback polling activation on connection loss
- [ ] 15.7 Add integration tests for JSON serializer plugin
- [ ] 15.8 Add integration tests for Protobuf serializer plugin
- [ ] 15.9 Add integration tests for Gzip and Brotli compressor plugins
- [ ] 15.10 Add integration tests for in-memory repository, outbox, and queue plugins
- [ ] 15.11 Add integration tests for end-to-end change tracking with in-memory storage and queue
- [ ] 15.12 Add integration tests for end-to-end change tracking with EF Core + PostgreSQL + queue
- [ ] 15.13 Add tests for standalone configuration and builder API
- [ ] 15.14 Add tests for outbox cleanup service
- [ ] 15.15 Add tests for concurrent change detection
- [ ] 15.16 Add tests for separate assembly loading (Serializers.Json, Serializers.Protobuf, Serializers.MessagePack, Compressors.Gzip, Compressors.Brotli, Compressors.Lz4, InMemory)

## 16. Documentation

- [ ] 16.1 Write getting started guide with quick-start example
- [ ] 16.2 Write configuration guide (standalone and DI modes)
- [ ] 16.3 Write plugin development guide for custom providers
- [ ] 16.4 Write serializer plugin guides (JSON, Protobuf, MessagePack — each as separate package)
- [ ] 16.5 Write compressor plugin guides (Gzip, Brotli, LZ4 — each as separate package)
- [ ] 16.6 Write in-memory plugin guide (testing and development)
- [ ] 16.7 Write EF Core integration guide
- [ ] 16.8 Write database migration guide for source/outbox tables
- [ ] 16.9 Write database trigger setup guide
