using Confluent.Kafka;
using Moq;
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
        Assert.That(options.KeySelector, Is.Not.Null);
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
    public void KafkaPublisher_NoLoggerFactory_ConstructsAndDisposesCleanly()
    {
        // Legacy call shape: `new KafkaPublisher(options)` with the optional loggerFactory omitted.
        // Verifies the new optional parameter doesn't break source-compat callers.
        Assert.DoesNotThrow(() =>
        {
            using var publisher = new KafkaPublisher(new KafkaPublisherOptions());
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
    public void KafkaPublisherOptions_DefaultKeySelector_UsesEntityTypeAndId()
    {
        var options = new KafkaPublisherOptions();
        var envelope = new MessageEnvelope { EntityType = "User", EntityId = "user-123" };

        var key = options.KeySelector(envelope);

        Assert.That(key, Is.EqualTo("User:user-123"));
    }

    [Test]
    public void KafkaPublisherOptions_CustomKeySelector_IsUsed()
    {
        var options = new KafkaPublisherOptions
        {
            KeySelector = static envelope => envelope.EntityId
        };
        var envelope = new MessageEnvelope { EntityType = "User", EntityId = "user-123" };

        var key = options.KeySelector(envelope);

        Assert.That(key, Is.EqualTo("user-123"));
    }

    [Test]
    public async Task PublishAsync_PassesKeySelectorOutputToProducer()
    {
        string? capturedKey = null;
        var mockProducer = new Mock<IProducer<string, byte[]>>();
        mockProducer
            .Setup(p => p.ProduceAsync(
                It.IsAny<string>(),
                It.IsAny<Message<string, byte[]>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Message<string, byte[]>, CancellationToken>(
                (_, msg, _) => capturedKey = msg.Key)
            .ReturnsAsync(new DeliveryResult<string, byte[]>());

        var options = new KafkaPublisherOptions
        {
            KeySelector = static envelope => $"tenant-{envelope.EntityId}"
        };
        var publisher = new KafkaPublisher(options);
        SetProducerViaReflection(publisher, mockProducer.Object);

        await publisher.PublishAsync(new MessageEnvelope
        {
            EntityType    = "Order",
            EntityId      = "acme",
            ChangeType    = ChangeType.Insert,
            CorrelationId = Guid.NewGuid(),
            Payload       = [1]
        });

        Assert.That(capturedKey, Is.EqualTo("tenant-acme"));
    }

    private static void SetProducerViaReflection(KafkaPublisher publisher, IProducer<string, byte[]> producer)
    {
        var field = typeof(KafkaPublisher).GetField(
            "_producer",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field!.SetValue(publisher, producer);
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
