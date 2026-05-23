using Microsoft.Extensions.Logging.Abstractions;

namespace RayTree.Plugins.Kafka.Tests;

public class KafkaTopicProbeTests
{
    [Test]
    public void WaitForTopicAsync_NonPositiveInterval_ThrowsArgumentOutOfRange()
    {
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            KafkaTopicProbe.WaitForTopicAsync(
                bootstrapServers: "localhost:9092",
                topic: "irrelevant",
                interval: TimeSpan.Zero,
                timeout: null,
                logger: null,
                cancellationToken: CancellationToken.None));

        Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            KafkaTopicProbe.WaitForTopicAsync(
                bootstrapServers: "localhost:9092",
                topic: "irrelevant",
                interval: TimeSpan.FromSeconds(-1),
                timeout: null,
                logger: null,
                cancellationToken: CancellationToken.None));
    }

    [Test]
    public void WaitForTopicAsync_NonPositiveTimeout_ThrowsArgumentOutOfRange()
    {
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            KafkaTopicProbe.WaitForTopicAsync(
                bootstrapServers: "localhost:9092",
                topic: "irrelevant",
                interval: TimeSpan.FromSeconds(1),
                timeout: TimeSpan.Zero,
                logger: null,
                cancellationToken: CancellationToken.None));

        Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            KafkaTopicProbe.WaitForTopicAsync(
                bootstrapServers: "localhost:9092",
                topic: "irrelevant",
                interval: TimeSpan.FromSeconds(1),
                timeout: TimeSpan.FromSeconds(-1),
                logger: null,
                cancellationToken: CancellationToken.None));
    }

    [Test]
    public void WaitForTopicAsync_PreCancelledToken_ThrowsImmediatelyWithoutGetMetadata()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Validation succeeds (positive interval), then the cancellation check fires
        // BEFORE any AdminClient is built or GetMetadata is called.
        Assert.ThrowsAsync<OperationCanceledException>(() =>
            KafkaTopicProbe.WaitForTopicAsync(
                bootstrapServers: "localhost:9092",
                topic: "irrelevant",
                interval: TimeSpan.FromSeconds(1),
                timeout: TimeSpan.FromSeconds(5),
                logger: NullLogger.Instance,
                cancellationToken: cts.Token));
    }
}
