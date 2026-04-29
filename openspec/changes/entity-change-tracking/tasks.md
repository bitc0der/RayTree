## 1. Project Setup

- [ ] 1.1 Create solution structure with projects: RayTree.Core, RayTree.EntityFrameworkCore, RayTree.Hosting, RayTree.Plugins.PostgreSQL, RayTree.Plugins.RabbitMQ, RayTree.Plugins.Kafka, RayTree.Plugins.Serializers, RayTree.Plugins.Compressors
- [ ] 1.2 Add shared NuGet package references (Microsoft.EntityFrameworkCore, Npgsql, RabbitMQ.Client, Confluent.Kafka, protobuf-net, MessagePack, lz4net)
- [ ] 1.3 Set up project references and solution file
- [ ] 1.4 Configure common build properties and versioning

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

## 9. Serializer Plugins Assembly

- [ ] 9.1 Create `RayTree.Plugins.Serializers` project with no dependencies on RayTree.Core beyond interfaces
- [ ] 9.2 Implement JSON serializer plugin using System.Text.Json
- [ ] 9.3 Implement Protobuf serializer plugin using protobuf-net
- [ ] 9.4 Implement MessagePack serializer plugin using MessagePack-CSharp
- [ ] 9.5 Implement serializer extension methods on configuration builder (UseJsonSerializer, UseProtobufSerializer, UseMessagePackSerializer)

## 10. Compressor Plugins Assembly

- [ ] 10.1 Create `RayTree.Plugins.Compressors` project with no dependencies on RayTree.Core beyond interfaces
- [ ] 10.2 Implement Gzip compressor plugin using System.IO.Compression
- [ ] 10.3 Implement Brotli compressor plugin using System.IO.Compression
- [ ] 10.4 Implement LZ4 compressor plugin using lz4net
- [ ] 10.5 Implement NoOp compressor plugin (pass-through, in core)
- [ ] 10.6 Implement compressor extension methods on configuration builder (UseGzipCompressor, UseBrotliCompressor, UseLz4Compressor, UseNoOpCompressor)

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

## 14. Testing

- [ ] 14.1 Add unit tests for core abstractions and EntityChangeTracker
- [ ] 14.2 Add unit tests for serialization/compression pipeline
- [ ] 14.3 Add unit tests for EF Core interceptor with in-memory provider
- [ ] 14.4 Add integration tests for PostgreSQL repository and outbox plugins
- [ ] 14.5 Add integration tests for RabbitMQ publisher plugin
- [ ] 14.6 Add integration tests for Kafka publisher plugin
- [ ] 14.7 Add integration tests for JSON serializer plugin
- [ ] 14.8 Add integration tests for Protobuf serializer plugin
- [ ] 14.9 Add integration tests for Gzip and Brotli compressor plugins
- [ ] 14.10 Add integration tests for end-to-end change tracking with EF Core + PostgreSQL + queue
- [ ] 14.11 Add tests for standalone configuration and builder API
- [ ] 14.12 Add tests for outbox cleanup service
- [ ] 14.13 Add tests for concurrent change detection
- [ ] 14.14 Add tests for separate assembly loading (Serializers, Compressors)

## 15. Documentation

- [ ] 15.1 Write getting started guide with quick-start example
- [ ] 15.2 Write configuration guide (standalone and DI modes)
- [ ] 15.3 Write plugin development guide for custom providers
- [ ] 15.4 Write serializer plugin guide (JSON, Protobuf, MessagePack)
- [ ] 15.5 Write compressor plugin guide (Gzip, Brotli, LZ4)
- [ ] 15.6 Write EF Core integration guide
- [ ] 15.7 Write database migration guide for source/outbox tables
- [ ] 15.8 Write database trigger setup guide
