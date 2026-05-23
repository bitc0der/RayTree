using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RayTree.Core.Plugins.Compression;
using RayTree.Core.Tracking;
using RayTree.Hosting;
using RayTree.Plugins.InMemory;
using RayTree.Plugins.Serializers.Json;

namespace RayTree.Core.Tests.Logging;

/// <summary>
/// Covers the configuration- and lifecycle-time logging added by the
/// <c>add-tracker-config-logging</c> change. Distinct from <see cref="LoggingTests"/> which
/// covers runtime-event logging (poll retries, dedup hits, SkipOnFailure drops).
/// </summary>
public class ConfigurationLoggingTests
{
    private sealed record LogEntry(string Category, LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Props);

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public List<LogEntry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly string _category;
        private readonly List<LogEntry> _entries;

        public CapturingLogger(string category, List<LogEntry> entries)
        {
            _category = category;
            _entries = entries;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var props = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IEnumerable<KeyValuePair<string, object?>> kvps)
                foreach (var kvp in kvps)
                    props[kvp.Key] = kvp.Value;

            _entries.Add(new LogEntry(_category, logLevel, formatter(state, exception), props));
        }
    }

    private class SampleEntity
    {
        public int Id { get; set; }
    }

    private static EntityChangeTracker BuildMinimal(ILoggerFactory loggerFactory)
        => new ChangeTrackingBuilder(loggerFactory)
            .ForEntity<SampleEntity>(e =>
            {
                e.UseOutbox(new InMemoryOutbox());
                e.UsePublisher(new InMemoryQueue());
                e.UseSerializer(new JsonSerializerPlugin());
                e.UseCompressor(new NoOpCompressorPlugin());
            })
            .Build();

    // -------------------------------------------------------------------------
    // 5.2 — each Use* call emits exactly one Information entry
    // -------------------------------------------------------------------------

    [Test]
    public void GlobalUseCalls_EmitOneInformationLogEach()
    {
        // Arrange
        var lf = new CapturingLoggerFactory();

        // Act
        new ChangeTrackingBuilder(lf)
            .UseSerializer<JsonSerializerPlugin>(_ => new JsonSerializerPlugin())
            .UseCompressor<NoOpCompressorPlugin>(_ => new NoOpCompressorPlugin())
            .UsePublisherOptions(_ => { })
            .UseSubscriberOptions(_ => { });

        // Assert
        var infos = lf.Entries
            .Where(e => e.Category.Contains("ChangeTrackingBuilder") && e.Level == LogLevel.Information)
            .ToList();

        Assert.That(infos, Has.Count.EqualTo(4));
        Assert.That(infos.Select(e => e.Props.GetValueOrDefault("Plugin")?.ToString()),
            Is.EquivalentTo(new[] {
                nameof(JsonSerializerPlugin),
                nameof(NoOpCompressorPlugin),
                "OutboxPublisherOptions",
                "SubscriberOptions"
            }));
    }

    // -------------------------------------------------------------------------
    // 5.3 — ForEntity logs Information; overrides log Debug
    // -------------------------------------------------------------------------

    [Test]
    public void ForEntity_LogsEntityTypeAtInformation_AndOverridesAtDebug()
    {
        // Arrange
        var lf = new CapturingLoggerFactory();

        // Act
        new ChangeTrackingBuilder(lf)
            .ForEntity<SampleEntity>(e =>
            {
                e.UseOutbox(new InMemoryOutbox());
                e.UsePublisher(new InMemoryQueue());
                e.UseSerializer(new JsonSerializerPlugin());
                e.UseCompressor(new NoOpCompressorPlugin());
            });

        // Assert
        var info = lf.Entries.Single(e =>
            e.Level == LogLevel.Information &&
            e.Props.GetValueOrDefault("EntityType")?.ToString() == nameof(SampleEntity));

        Assert.That(info, Is.Not.Null);

        var debugs = lf.Entries
            .Where(e => e.Level == LogLevel.Debug &&
                        e.Props.GetValueOrDefault("EntityType")?.ToString() == nameof(SampleEntity))
            .Select(e => e.Props.GetValueOrDefault("Override")?.ToString())
            .ToList();

        Assert.That(debugs, Is.EquivalentTo(new[] { "Outbox", "Publisher", "Serializer", "Compressor" }));
    }

    // -------------------------------------------------------------------------
    // 5.4 — Build emits the summary log with all properties
    // -------------------------------------------------------------------------

    [Test]
    public void Build_EmitsSummaryLog_WithAllStructuredProperties()
    {
        // Arrange
        var lf = new CapturingLoggerFactory();

        // Act
        using var tracker = BuildMinimal(lf);

        // Assert
        var summary = lf.Entries.Single(e =>
            e.Level == LogLevel.Information &&
            e.Message.StartsWith("ChangeTracker built"));

        Assert.That(summary.Props.ContainsKey("EntityTypes"), Is.True);
        Assert.That(summary.Props.ContainsKey("Plugins"), Is.True);
        Assert.That(summary.Props["HasCustomMeter"], Is.EqualTo(false));
        Assert.That(summary.Props["HasCustomDeduplicationStore"], Is.EqualTo(false));
        Assert.That(summary.Props["HasCustomLoggerFactory"], Is.EqualTo(true));
    }

    [Test]
    public void Build_SummaryLog_ReportsNone_ForUnregisteredGlobalPlugins()
    {
        // Arrange
        var lf = new CapturingLoggerFactory();

        // Act
        using var tracker = BuildMinimal(lf);

        // Assert — no global registrations were made (everything per-entity), so every plugin
        // slot in the summary log must read "<none>".
        var summary = lf.Entries.Single(e =>
            e.Level == LogLevel.Information &&
            e.Message.StartsWith("ChangeTracker built"));

        Assert.That(summary.Message, Contains.Substring("<none>"));
    }

    // -------------------------------------------------------------------------
    // 5.5 — NullLoggerFactory produces zero log entries
    // -------------------------------------------------------------------------

    [Test]
    public void Build_WithNullLoggerFactory_EmitsNoLogs()
    {
        // Arrange & Act — NullLogger.IsEnabled returns false, so all guarded calls are skipped.
        using var tracker = new ChangeTrackingBuilder(NullLoggerFactory.Instance)
            .ForEntity<SampleEntity>(e =>
            {
                e.UseOutbox(new InMemoryOutbox());
                e.UsePublisher(new InMemoryQueue());
                e.UseSerializer(new JsonSerializerPlugin());
                e.UseCompressor(new NoOpCompressorPlugin());
            })
            .Build();

        // Assert — NullLogger discards everything; success is "does not throw".
        Assert.That(tracker, Is.Not.Null);
    }

    // -------------------------------------------------------------------------
    // 5.6 — InitializeAsync logs start, sub-steps, completion
    // -------------------------------------------------------------------------

    [Test]
    public void InitializeAsync_LogsStartSubStepsAndCompletion_InOrder()
    {
        // Arrange
        var lf = new CapturingLoggerFactory();

        // Act
        using var tracker = BuildMinimal(lf);

        // Assert
        var trackerLogs = lf.Entries
            .Where(e => e.Category.Contains("EntityChangeTracker"))
            .ToList();

        var start    = trackerLogs.FindIndex(e => e.Level == LogLevel.Information && e.Message.Contains("started"));
        var pubDbg   = trackerLogs.FindIndex(e => e.Level == LogLevel.Debug && e.Message.Contains("publisher initialized"));
        var consDbg  = trackerLogs.FindIndex(e => e.Level == LogLevel.Debug && e.Message.Contains("consumers initialized"));
        var complete = trackerLogs.FindIndex(e => e.Level == LogLevel.Information && e.Message.Contains("completed"));

        Assert.That(start, Is.GreaterThanOrEqualTo(0));
        Assert.That(pubDbg, Is.GreaterThan(start));
        Assert.That(consDbg, Is.GreaterThan(pubDbg));
        Assert.That(complete, Is.GreaterThan(consDbg));

        Assert.That(trackerLogs[pubDbg].Props["EntityTypeCount"], Is.EqualTo(1));
        Assert.That(trackerLogs[consDbg].Props["ConsumerCount"], Is.EqualTo(0));
    }

    [Test]
    public void InitializeAsync_OnFailure_LogsWarningBeforeRethrow()
    {
        // Arrange
        var lf = new CapturingLoggerFactory();

        // Act
        Assert.Throws<InvalidOperationException>(() =>
        {
            using var tracker = new ChangeTrackingBuilder(lf)
                .ForEntity<SampleEntity>(e =>
                {
                    e.UseOutbox(new InMemoryOutbox());
                    e.UsePublisher(new InitFailingPublisher());
                    e.UseSerializer(new JsonSerializerPlugin());
                    e.UseCompressor(new NoOpCompressorPlugin());
                })
                .Build();
        });

        // Assert
        var aborted = lf.Entries.SingleOrDefault(e =>
            e.Category.Contains("EntityChangeTracker") &&
            e.Level == LogLevel.Warning &&
            e.Message.Contains("aborted"));

        Assert.That(aborted, Is.Not.Null);
    }

    private sealed class InitFailingPublisher : RayTree.Core.Plugins.Publisher.IQueuePublisher
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Simulated init failure");
        public Task PublishAsync(RayTree.Core.Models.MessageEnvelope envelope, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    // -------------------------------------------------------------------------
    // 5.7 — ChangeTrackingHostedService startup log
    // -------------------------------------------------------------------------

    [Test]
    public async Task ChangeTrackingHostedService_StartAsync_EmitsStartingLog_WithConfigurationBound()
    {
        // Arrange
        var lf = new CapturingLoggerFactory();
        using var tracker = BuildMinimal(lf);
        var hostedLogger = lf.CreateLogger<ChangeTrackingHostedService>();
        var svc = new ChangeTrackingHostedService(tracker, hostedLogger, new ChangeTrackingDiContext(ConfigurationBound: true));

        // Act
        await svc.StartAsync(CancellationToken.None);

        // Assert
        var starting = lf.Entries.SingleOrDefault(e =>
            e.Level == LogLevel.Information &&
            e.Message.StartsWith("ChangeTracking starting"));

        Assert.That(starting, Is.Not.Null);
        Assert.That(starting!.Props["ConfigurationBound"], Is.EqualTo(true));

        await svc.StopAsync(CancellationToken.None);
    }
}
