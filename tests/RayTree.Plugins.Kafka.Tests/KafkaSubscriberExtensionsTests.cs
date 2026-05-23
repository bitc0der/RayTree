using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RayTree.Plugins.Kafka.Tests;

public class KafkaSubscriberExtensionsTests
{
    [Test]
    public void KafkaConsumer_NoLoggerFactory_StillConstructs()
    {
        // The subscriber extension UseKafka<TEntity> accepts an optional ILoggerFactory? default null.
        // Verify the underlying KafkaConsumer construction succeeds with NullLoggerFactory (the
        // default the extension forwards), preserving back-compat for fluent-builder callers
        // that don't supply a factory.
        Assert.DoesNotThrow(() =>
        {
            using var consumer = new KafkaConsumer(new KafkaConsumerOptions(), NullLoggerFactory.Instance);
        });
    }

    [Test]
    public void KafkaConsumer_WithCustomLoggerFactory_UsesItForInternalLogger()
    {
        // Verify that the consumer's internal _logger field is sourced from the supplied factory
        // (mirrors what UseKafka<TEntity>(configure, loggerFactory) forwards into the constructor).
        // Without this guarantee the spec's logging requirements are unsatisfiable for fluent-builder callers.
        // Assert by routing a log entry through the consumer's internal logger and observing it
        // arrives at the supplied provider's capture sink.
        var capture = new ConcurrentQueue<string>();
        using var factory = LoggerFactory.Create(b => b
            .SetMinimumLevel(LogLevel.Trace)
            .AddProvider(new TestCaptureProvider(capture)));

        using var consumer = new KafkaConsumer(new KafkaConsumerOptions(), factory);

        var loggerField = typeof(KafkaConsumer).GetField(
            "_logger",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.That(loggerField, Is.Not.Null, "KafkaConsumer._logger field not found via reflection");

        var logger = (ILogger<KafkaConsumer>?)loggerField!.GetValue(consumer);
        Assert.That(logger, Is.Not.Null);

        logger!.LogInformation("probe-test-message");

        Assert.That(capture, Has.Count.EqualTo(1),
            "Consumer's internal logger should route through the supplied factory, not NullLoggerFactory");
        Assert.That(capture.TryDequeue(out var msg) && msg!.Contains("probe-test-message"));
    }

    private sealed class TestCaptureProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _entries;
        public TestCaptureProvider(ConcurrentQueue<string> entries) => _entries = entries;
        public ILogger CreateLogger(string categoryName) => new TestCaptureLogger(_entries);
        public void Dispose() { }

        private sealed class TestCaptureLogger : ILogger
        {
            private readonly ConcurrentQueue<string> _entries;
            public TestCaptureLogger(ConcurrentQueue<string> entries) => _entries = entries;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
                => _entries.Enqueue(formatter(state, exception));
        }
    }
}
