using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RayTree.Core.Distribution;
using RayTree.Core.Telemetry;
using RayTree.Core.Handling;
using RayTree.Core.Models;
using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Compression;
using RayTree.Core.Plugins.Publisher;
using RayTree.Core.Tracking;
using RayTree.Plugins.InMemory;
using RayTree.Plugins.Serializers.Json;

namespace RayTree.Core.Tests;

/// <summary>
/// Verifies structured logging behaviour introduced by the support-logging change:
///  - NullLogger default (no factory supplied) produces no errors at build time
///  - OutboxPublisherService logs Warning on a retried publish failure
///  - ChangeSubscriber logs Error when SkipOnFailure silently drops a message
/// </summary>
public class LoggingTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>Captures every Log call so tests can assert on level and exceptions.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, exception));
    }

    /// <summary>
    /// Minimal <see cref="ILoggerFactory"/> that always returns the same pre-built logger
    /// regardless of the requested category name.
    /// </summary>
    private sealed class FixedLoggerFactory<T> : ILoggerFactory
    {
        private readonly ILogger<T> _logger;
        public FixedLoggerFactory(ILogger<T> logger) => _logger = logger;
        public ILogger CreateLogger(string categoryName) => (ILogger)_logger;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }

    /// <summary>
    /// Queue publisher that throws <see cref="InvalidOperationException"/> for the first
    /// <paramref name="failCount"/> calls to <see cref="PublishAsync"/>, then succeeds.
    /// </summary>
    private sealed class FailingPublisher : IQueuePublisher
    {
        private int _calls;
        private readonly int _failCount;

        public FailingPublisher(int failCount) => _failCount = failCount;

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PublishAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) <= _failCount)
                throw new InvalidOperationException("Simulated publish failure");
            return Task.CompletedTask;
        }
    }

    private class SampleEntity
    {
        public int Id { get; set; }
    }

    // -------------------------------------------------------------------------
    // 10.1  NullLogger default — building without UseLoggerFactory must not throw
    // -------------------------------------------------------------------------

    [Test]
    public void Build_WithoutLoggerFactory_Succeeds()
    {
        // Arrange & Act — no UseLoggerFactory call; should default to NullLoggerFactory
        using var tracker = new ChangeTrackingBuilder()
            .ForEntity<SampleEntity>(e =>
            {
                e.UseOutbox(new InMemoryOutbox());
                e.UsePublisher(new InMemoryQueue());
                e.UseSerializer(new JsonSerializerPlugin());
                e.UseCompressor(new NoOpCompressorPlugin());
            })
            .Build();

        // Assert — construction and initialisation succeed
        Assert.That(tracker, Is.Not.Null);
    }

    // -------------------------------------------------------------------------
    // 10.2  OutboxPublisherService logs Warning on a retried publish failure
    // -------------------------------------------------------------------------

    [Test]
    public async Task OutboxPublisherService_PublishRetry_LogsWarning()
    {
        // Arrange
        var recordingLogger  = new RecordingLogger<OutboxPublisherService>();
        var loggerFactory    = new FixedLoggerFactory<OutboxPublisherService>(recordingLogger);
        var outbox           = new InMemoryOutbox();
        var failingPublisher = new FailingPublisher(failCount: 1); // fail once, succeed on second call

        var publisher = new ChangePublisher(loggerFactory, new RayTreeMeter());
        publisher.RegisterOutbox(typeof(SampleEntity), outbox);
        publisher.RegisterPublisher(typeof(SampleEntity), failingPublisher);
        publisher.RegisterSerializer(typeof(SampleEntity), new JsonSerializerPlugin());
        publisher.RegisterCompressor(typeof(SampleEntity), new NoOpCompressorPlugin());
        publisher.Options.PollingInterval = TimeSpan.FromMilliseconds(20);
        publisher.Options.RetryDelay      = TimeSpan.FromMilliseconds(1);
        publisher.Options.MaxRetryCount   = 3;

        // Seed one change
        await outbox.WriteAsync(new EntityChange<SampleEntity>
        {
            EntityType    = typeof(SampleEntity).AssemblyQualifiedName!,
            EntityId      = "1",
            ChangeType    = ChangeType.Insert,
            CorrelationId = Guid.NewGuid(),
            Version       = 1,
            State         = new SampleEntity { Id = 1 }
        });

        // Act — start publisher services (InitializeAsync starts the polling loop)
        await publisher.InitializeAsync();

        // Wait up to 3 seconds for the Warning to appear
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline &&
               !recordingLogger.Entries.Any(e => e.Level == LogLevel.Warning))
        {
            await Task.Delay(20);
        }

        publisher.Dispose();

        // Assert — at least one Warning was emitted during the retry
        Assert.That(
            recordingLogger.Entries.Any(e => e.Level == LogLevel.Warning),
            Is.True,
            "Expected a Warning log entry from the failed publish retry");
    }

    // -------------------------------------------------------------------------
    // 10.3  ChangeSubscriber logs Debug on a duplicate CorrelationId
    // -------------------------------------------------------------------------

    [Test]
    public async Task ChangeSubscriber_DuplicateMessage_LogsDebug()
    {
        // Arrange
        var recordingLogger = new RecordingLogger<ChangeSubscriber>();
        var subscriber      = new ChangeSubscriber(recordingLogger, new RayTreeMeter());

        var correlationId = Guid.NewGuid();
        var envelope = new MessageEnvelope
        {
            EntityType    = typeof(SampleEntity).AssemblyQualifiedName!,
            EntityId      = "1",
            ChangeType    = ChangeType.Insert,
            CorrelationId = correlationId
        };

        // First delivery — marks the correlation ID as processed
        await subscriber.ProcessMessageAsync(envelope);
        recordingLogger.Entries.Clear();

        // Act — second delivery with the same CorrelationId
        await subscriber.ProcessMessageAsync(envelope);

        // Assert — a Debug entry was emitted for the duplicate
        Assert.That(
            recordingLogger.Entries.Any(e => e.Level == LogLevel.Debug),
            Is.True,
            "Expected a Debug log entry for the duplicate CorrelationId");
    }

    // -------------------------------------------------------------------------
    // 10.4  ChangeSubscriber logs Debug when no handlers are registered
    // -------------------------------------------------------------------------

    [Test]
    public async Task ChangeSubscriber_NoHandlers_LogsDebug()
    {
        // Arrange — subscriber has no entity registrations at all
        var recordingLogger = new RecordingLogger<ChangeSubscriber>();
        var subscriber      = new ChangeSubscriber(recordingLogger, new RayTreeMeter());

        var envelope = new MessageEnvelope
        {
            EntityType    = typeof(SampleEntity).AssemblyQualifiedName!,
            EntityId      = "2",
            ChangeType    = ChangeType.Update,
            CorrelationId = Guid.NewGuid()
        };

        // Act
        await subscriber.ProcessMessageAsync(envelope);

        // Assert — a Debug entry was emitted because no handlers matched
        Assert.That(
            recordingLogger.Entries.Any(e => e.Level == LogLevel.Debug),
            Is.True,
            "Expected a Debug log entry when no handlers are registered for the entity type");
    }

    // -------------------------------------------------------------------------
    // 10.5  ChangeSubscriber logs Error when SkipOnFailure drops a message
    // -------------------------------------------------------------------------

    [Test]
    public async Task ChangeSubscriber_SkipOnFailure_LogsError()
    {
        // Arrange
        var recordingLogger = new RecordingLogger<ChangeSubscriber>();
        var options         = new SubscriberOptions { MaxRetries = 0, SkipOnFailure = true };
        var subscriber      = new ChangeSubscriber(recordingLogger, new RayTreeMeter(), options: options);

        subscriber.RegisterQueue<SampleEntity>(new InMemoryQueue());
        subscriber.OnChange<SampleEntity>(changeType: null, (_, _) =>
            throw new InvalidOperationException("Simulated handler failure"));

        var envelope = new MessageEnvelope
        {
            EntityType    = typeof(SampleEntity).AssemblyQualifiedName!,
            EntityId      = "99",
            ChangeType    = ChangeType.Update,
            CorrelationId = Guid.NewGuid()
        };

        // Act
        await subscriber.ProcessMessageAsync(envelope);

        // Assert — one Error entry logged before the message was dropped
        Assert.That(
            recordingLogger.Entries.Any(e => e.Level == LogLevel.Error),
            Is.True,
            "Expected an Error log entry when SkipOnFailure drops the message");
    }
}
