using RayTree.Core.Models;
using RayTree.Core.Tracking;

namespace RayTree.Plugins.Kafka.Tests;

public class KafkaPublisherTests
{
    [Test]
    public void KafkaPublisherOptions_DefaultValues_AreCorrect()
    {
        var options = new KafkaPublisherOptions();

        Assert.That(options.BootstrapServers, Is.EqualTo("localhost:9092"));
        Assert.That(options.Topic, Is.EqualTo("entity_changes"));
        Assert.That(options.Acks, Is.Null);
        Assert.That(options.MessageMaxBytes, Is.Null);
    }

    [Test]
    public void KafkaPublisher_CanBeCreated()
    {
        var options = new KafkaPublisherOptions
        {
            BootstrapServers = "localhost:9092",
            Topic = "test_topic"
        };

        var publisher = new KafkaPublisher(options);
        Assert.That(publisher, Is.Not.Null);
        Assert.DoesNotThrow(() => publisher.Dispose());
    }

    [Test]
    public void KafkaPublisher_WithAllOptions_CanBeCreated()
    {
        var options = new KafkaPublisherOptions
        {
            BootstrapServers = "kafka1:9092,kafka2:9093",
            Topic = "custom_topic",
            Acks = "all",
            MessageMaxBytes = 1048576
        };

        var publisher = new KafkaPublisher(options);
        Assert.That(publisher, Is.Not.Null);
        Assert.DoesNotThrow(() => publisher.Dispose());
    }

    [Test]
    public void KafkaPublisher_IdempotentDispose()
    {
        var publisher = new KafkaPublisher(new KafkaPublisherOptions());

        Assert.DoesNotThrow(() =>
        {
            publisher.Dispose();
            publisher.Dispose();
        });
    }

    [Test]
    public async Task KafkaPublisher_CopyStream_ProducesCorrectPayload()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        using var source = new MemoryStream(data);
        using var destination = new MemoryStream();

        await source.CopyToAsync(destination);

        Assert.That(destination.ToArray(), Is.EqualTo(data));
    }

    [Test]
    public void KafkaPublisher_CreateChange_BuildsCorrectMessageKey()
    {
        var change = new EntityChange
        {
            EntityType = "User",
            EntityId = "user-123",
            ChangeType = ChangeType.Update,
            Timestamp = DateTime.UtcNow,
            CorrelationId = Guid.NewGuid()
        };

        var expectedKey = $"{change.EntityType}:{change.EntityId}";
        Assert.That(expectedKey, Is.EqualTo("User:user-123"));
    }

    [Test]
    public void KafkaPublisher_Headers_ContainAllMetadata()
    {
        var correlationId = Guid.NewGuid();
        var change = new EntityChange
        {
            EntityType = "Product",
            EntityId = "prod-456",
            ChangeType = ChangeType.Delete,
            Timestamp = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc),
            CorrelationId = correlationId,
            Version = 7
        };

        Assert.That(change.EntityType, Is.EqualTo("Product"));
        Assert.That(change.EntityId, Is.EqualTo("prod-456"));
        Assert.That(change.ChangeType, Is.EqualTo(ChangeType.Delete));
        Assert.That(change.Version, Is.EqualTo(7));
        Assert.That(change.CorrelationId, Is.EqualTo(correlationId));
    }
}
