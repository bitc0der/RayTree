## 1. Project Setup

- [x] 1.1 Create `Directory.Build.props` with common MSBuild settings (target framework, nullable, implicit usings, warning level, treat warnings as errors)
- [x] 1.2 Create `Directory.Packages.props` with centralized package version management (CentralPackageManagement enabled)
- [x] 1.3 Create solution structure with projects: RayTree.Core, RayTree.EntityFrameworkCore, RayTree.Hosting, RayTree.Subscriber, RayTree.Plugins.PostgreSQL, RayTree.Plugins.RabbitMQ, RayTree.Plugins.Kafka, RayTree.Plugins.InMemory, RayTree.Plugins.Serializers.Json, RayTree.Plugins.Serializers.Protobuf, RayTree.Plugins.Serializers.MessagePack, RayTree.Plugins.Compressors.Gzip, RayTree.Plugins.Compressors.Brotli, RayTree.Plugins.Compressors.Lz4
- [x] 1.4 Define all package versions in `Directory.Packages.props` (Microsoft.EntityFrameworkCore, Npgsql, RabbitMQ.Client, Confluent.Kafka, protobuf-net, MessagePack, lz4net, System.Text.Json, System.IO.Compression)
- [x] 1.5 Set up project references and solution file
- [x] 1.6 Configure per-project .csproj files with package references using `VersionOverride` only when needed, otherwise inheriting from central management
- [x] 1.7 Add NUnit test packages to `Directory.Packages.props` (NUnit, NUnit.Analyzers, NUnit3TestAdapter, Microsoft.NET.Test.Sdk)
- [x] 1.8 Configure test project templates with NUnit + Moq + Testcontainers

## 2. Core Abstractions

- [x] 2.1 Define `IEntityChangeTracker` interface with change detection methods
- [x] 2.2 Define `IRepository` interface for entity persistence operations
- [x] 2.3 Define `IOutbox` interface for change storage operations
- [x] 2.4 Define `IQueuePublisher` interface for message publishing
- [x] 2.5 Define `IChangeSerializer` interface for message serialization
- [x] 2.6 Define `IChangeCompressor` interface for message compression
- [x] 2.7 Define `EntityChange` model with metadata fields (change_type, timestamp, version, correlation_id, entity_type)
- [x] 2.8 Define `ChangeType` enum (Insert, Update, Delete)
- [x] 2.9 Define `EntityConfiguration` model for per-entity plugin settings

## 3. Core Implementation

- [x] 3.1 Implement `EntityChangeTracker` as the core change detection engine
- [x] 3.2 Implement thread-safe change capture with concurrent collections
- [x] 3.3 Implement correlation ID generation and propagation across batched changes
- [x] 3.4 Implement stream-based serialization/compression pipeline (serialize → compress → publish via chained streams, zero intermediate allocations)
- [x] 3.5 Implement outbox query interface with filtering by published status, entity type, change type, and date range
- [x] 3.6 Implement outbox cleanup service with configurable retention period

## 4. EF Core Integration

- [x] 4.1 Implement `EntityChangeInterceptor` implementing `ISaveChangesInterceptor`
- [x] 4.2 Implement `SavingChanges` detection for Added, Modified, Deleted entities
- [x] 4.3 Implement `SavedChanges` outbox write in same transaction
- [x] 4.4 Implement async interceptor methods for `SaveChangesAsync`
- [x] 4.5 Implement entity type registration with filter support
- [x] 4.6 Implement `AddChangeTracking()` extension method on `IServiceCollection`
- [x] 4.7 Implement automatic interceptor attachment to registered DbContexts
- [x] 4.8 Implement opt-out mechanism for specific DbContexts
- [x] 4.9 Implement multi-DbContext support with independent configuration

## 5. Outbox Pattern Implementation

- [x] 5.1 Implement outbox schema generator for per-entity source and outbox tables
- [x] 5.2 Implement outbox table model with entity columns + metadata columns
- [x] 5.3 Implement atomic outbox write within EF Core transaction
- [x] 5.4 Implement rollback handling for outbox write failures
- [x] 5.5 Implement outbox polling with configurable interval and batch size

## 6. Change Distribution

- [x] 6.1 Implement `OutboxPublisherService` with polling loop
- [x] 6.2 Implement configurable polling interval and batch size
- [x] 6.3 Implement post-publish confirmation (mark outbox entry as published)
- [x] 6.4 Implement failed publish retry logic (leave unpublished for next poll)
- [x] 6.5 Implement graceful shutdown with in-flight operation completion
- [x] 6.6 Implement `NotificationBasedPublisher` with PostgreSQL LISTEN loop
- [x] 6.7 Implement `pg_notify` trigger generation for PostgreSQL outbox tables
- [x] 6.8 Implement notification payload parsing (entity type, outbox row ID, change type)
- [x] 6.9 Implement fallback polling that activates on LISTEN connection loss
- [x] 6.10 Implement automatic LISTEN reconnection with backlog scan on reconnect
- [x] 6.11 Implement `.UseNotificationChannel()` and `.WithFallbackPolling()` fluent API on PostgreSQL outbox config
- [x] 6.12 Implement multi-channel LISTEN support with notification routing
- [x] 6.13 Implement notification trigger DDL migration scripts (create/drop)

## 7. Plugin System

- [x] 7.1 Implement plugin registration via `IChangeTrackingBuilder`
- [x] 7.2 Implement plugin interface validation at registration time
- [x] 7.3 Implement global plugin defaults and per-entity plugin overrides
- [x] 7.4 Implement `IChangeTrackingBuilder` fluent API (UseRepository, UseOutbox, UseQueue, UseSerializer, UseCompressor)

## 8. Built-in Data Plugins

- [x] 8.1 Implement PostgreSQL repository plugin using Npgsql
- [x] 8.2 Implement PostgreSQL outbox plugin with table-per-entity schema
- [x] 8.3 Implement RabbitMQ queue publisher plugin using RabbitMQ.Client
- [x] 8.4 Implement Kafka queue publisher plugin using Confluent.Kafka

## 9. Serializer Plugin Assemblies

- [x] 9.1 Create `RayTree.Plugins.Serializers.Json` project with only `RayTree.Core` dependency
- [x] 9.2 Implement JSON serializer using System.Text.Json
- [x] 9.3 Implement `.UseJsonSerializer()` extension method
- [x] 9.4 Create `RayTree.Plugins.Serializers.Protobuf` project with only `RayTree.Core` + protobuf-net dependency
- [x] 9.5 Implement Protobuf serializer using protobuf-net
- [x] 9.6 Implement `.UseProtobufSerializer()` extension method
- [x] 9.7 Create `RayTree.Plugins.Serializers.MessagePack` project with only `RayTree.Core` + MessagePack-CSharp dependency
- [x] 9.8 Implement MessagePack serializer using MessagePack-CSharp
- [x] 9.9 Implement `.UseMessagePackSerializer()` extension method

## 10. Compressor Plugin Assemblies

- [x] 10.1 Create `RayTree.Plugins.Compressors.Gzip` project with only `RayTree.Core` dependency
- [x] 10.2 Implement Gzip compressor using System.IO.Compression
- [x] 10.3 Implement `.UseGzipCompressor()` extension method
- [x] 10.4 Create `RayTree.Plugins.Compressors.Brotli` project with only `RayTree.Core` dependency
- [x] 10.5 Implement Brotli compressor using System.IO.Compression
- [x] 10.6 Implement `.UseBrotliCompressor()` extension method
- [x] 10.7 Create `RayTree.Plugins.Compressors.Lz4` project with only `RayTree.Core` + K4os.Compression.LZ4 dependency
- [x] 10.8 Implement LZ4 compressor using K4os.Compression.LZ4
- [x] 10.9 Implement `.UseLz4Compressor()` extension method
- [x] 10.10 Implement NoOp compressor in Core (pass-through)
- [x] 10.11 Implement `.UseNoOpCompressor()` extension method

## 10.5 In-Memory Plugins Assembly

- [x] 10.5.1 Create `RayTree.Plugins.InMemory` project with only `RayTree.Core` dependency
- [x] 10.5.2 Implement `InMemoryRepository` using `ConcurrentDictionary<TKey, TEntity>`
- [x] 10.5.3 Implement `InMemoryOutbox` using `ConcurrentDictionary<long, EntityChange>` with thread-safe query and cleanup
- [x] 10.5.4 Implement `InMemoryQueue` using `Channel<T>` with per-entity-type broadcast
- [x] 10.5.5 Implement `.UseInMemoryRepository()`, `.UseInMemoryOutbox()`, `.UseInMemoryQueue()` fluent API methods
- [x] 10.5.6 Implement mixed configuration support (e.g., in-memory repo + external queue)
- [x] 10.5.7 Implement `.ConsumeFromInMemory()` and `.Subscribe<T>()` for subscriber side
- [x] 10.5.8 Implement in-memory deduplication store for subscriber
- [x] 10.5.9 Implement subscription handle with `.Unsubscribe()` support
- [x] 10.5.10 Implement transaction simulation for in-memory outbox rollback

## 11. .NET Host Integration

- [x] 11.1 Implement `OutboxPublisherHostedService` implementing `IHostedService`
- [x] 11.2 Implement hosted service start/stop lifecycle
- [x] 11.3 Implement `IOptions<ChangeTrackingOptions>` configuration binding
- [x] 11.4 Implement `IServiceCollection.AddChangeTracking()` with builder pattern
- [x] 11.5 Implement configuration support via appsettings.json and environment variables

## 12. Standalone Configuration

- [x] 12.1 Implement `ChangeTrackingConfiguration` builder class
- [x] 12.2 Implement fluent configuration methods (UseOutbox, UseQueue, UseSerializer, UseCompressor)
- [x] 12.3 Implement `Build()` method returning `EntityChangeTracker`
- [x] 12.4 Implement `StartPublisherAsync()` and `StopPublisherAsync()` for standalone publisher
- [x] 12.5 Implement `Dispose()` for resource cleanup

## 13. Database Triggers (Optional)

- [x] 13.1 Implement PostgreSQL trigger generator for source tables
- [x] 13.2 Implement trigger-based outbox write for non-EF Core changes
- [x] 13.3 Implement trigger polling mode for outbox publisher
- [x] 13.4 Document trigger installation and configuration steps

## 14. Subscriber Configuration

- [x] 14.1 Create `ChangeSubscriberConfiguration` builder class
- [x] 14.2 Implement `ConsumeEntity<T>()` method with per-entity source configuration
- [x] 14.3 Implement `FromKafka()`, `FromRabbitMq()`, and `FromInMemory()` entity-level consume source methods
- [x] 14.4 Implement per-entity serializer/compressor resolution matching publisher config
- [x] 14.5 Implement `OnChange<T>()` handler registration with optional ChangeType filter
- [x] 14.6 Implement handler invocation pipeline (decompress → deserialize → route to handlers)
- [x] 14.7 Implement deduplication store interface (`IDeduplicationStore`)
- [x] 14.8 Implement Redis deduplication store
- [x] 14.9 Implement per-entity error handling policies (retry, dead-letter, skip)
- [x] 14.10 Implement `ChangeSubscriberHostedService` for DI integration
- [x] 14.11 Implement `IServiceCollection.AddChangeSubscriber()` extension method
- [x] 14.12 Implement standalone subscriber `ChangeSubscriber` with `ProcessMessageAsync()`
- [x] 14.13 Implement multi-entity consume loop with parallel processing

## 14.5 Database DDL Initialization

- [x] 14.5.1 Implement `SourceTableDdlGenerator` for source table CREATE/DROP DDL
- [x] 14.5.2 Implement `CombinedDdlGenerator` orchestrating source + outbox + triggers
- [x] 14.5.3 Implement `IDdlExecutor` interface
- [x] 14.5.4 Implement `PostgreSqlDdlExecutor` with statement splitting and existence checks
- [x] 14.5.5 Implement `InitializeDatabaseAsync()` extension on `EntityChangeTracker`
- [x] 14.5.6 Implement `GenerateInitializationDdl()` and `GenerateDropDdl()` helpers
- [x] 14.5.7 Implement `DatabaseInitializationOptions` for configurable table naming

## 14.6 Attribute-Based DDL Generation

- [x] 14.6.1 Implement `AttributeBasedDdlGenerator` reading `[Table]`, `[Column]`, `[Key]`, `[Required]`, `[MaxLength]`, `[DatabaseGenerated]` attributes
- [x] 14.6.2 Implement C# type to PostgreSQL type mapping (int→SERIAL, string→VARCHAR/TEXT, DateTime→TIMESTAMPTZ, etc.)
- [x] 14.6.3 Implement EF Core `[Index]` attribute support via reflection (no hard dependency)
- [x] 14.6.4 Implement EF Core `[Precision]` attribute support via reflection
- [x] 14.6.5 Integrate attribute-based schema into `DatabaseInitializationExtensions` with `UseAttributeBasedSchema` flag
- [x] 14.6.6 Implement `GenerateInitializationDdlFor<T>()` extension for per-entity DDL generation
- [x] 14.6.7 Auto-detect `[Column]` names and `[Table]` schema/name in DDL output

## 15. Testing (NUnit)

- [x] 15.1 Add unit tests for core abstractions and EntityChangeTracker
- [x] 15.2 Add unit tests for serialization/compression pipeline
- [x] 15.15 Add tests for concurrent change detection
- [x] 15.13 Add tests for standalone configuration and builder API
- [x] 15.14 Add tests for outbox cleanup service
- [ ] 15.3 Add unit tests for EF Core interceptor with in-memory provider
- [x] 15.16 Add tests for separate assembly loading (Serializers.Json, Serializers.Protobuf, Serializers.MessagePack, Compressors.Gzip, Compressors.Brotli, Compressors.Lz4, InMemory)
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
