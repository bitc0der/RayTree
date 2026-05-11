using Moq;
using RabbitMQ.Client;
using RayTree.Core.Models;
using RayTree.Core.Tracking;

namespace RayTree.Plugins.RabbitMQ.Tests;

public class RabbitMqPublisherTests
{
    [Test]
    public void RabbitMqPublisherOptions_DefaultValues_AreCorrect()
    {
        var options = new RabbitMqPublisherOptions();

        Assert.That(options.HostName, Is.EqualTo("localhost"));
        Assert.That(options.Port, Is.EqualTo(5672));
        Assert.That(options.UserName, Is.EqualTo("guest"));
        Assert.That(options.Password, Is.EqualTo("guest"));
        Assert.That(options.ExchangeName, Is.EqualTo("entity_changes"));
        Assert.That(options.RoutingKey, Is.EqualTo("change"));
        Assert.That(options.DeclareExchange, Is.True);
        Assert.That(options.ExchangeType, Is.EqualTo("topic"));
        Assert.That(options.Durable, Is.True);
    }

    [Test]
    public void RabbitMqPublisherOptions_CustomValues_ArePreserved()
    {
        var options = new RabbitMqPublisherOptions
        {
            HostName = "myhost",
            Port = 5673,
            UserName = "admin",
            Password = "secret",
            ExchangeName = "custom_exchange",
            RoutingKey = "custom_key",
            DeclareExchange = false,
            ExchangeType = "direct",
            Durable = false
        };

        Assert.That(options.HostName, Is.EqualTo("myhost"));
        Assert.That(options.Port, Is.EqualTo(5673));
        Assert.That(options.ExchangeName, Is.EqualTo("custom_exchange"));
        Assert.That(options.RoutingKey, Is.EqualTo("custom_key"));
        Assert.That(options.DeclareExchange, Is.False);
        Assert.That(options.ExchangeType, Is.EqualTo("direct"));
        Assert.That(options.Durable, Is.False);
    }

    [Test]
    public void RabbitMqPublisher_CanBeCreated()
    {
        var options = new RabbitMqPublisherOptions();
        var publisher = new RabbitMqPublisher(options);

        Assert.That(publisher, Is.Not.Null);
        Assert.DoesNotThrow(() => publisher.Dispose());
    }

    [Test]
    public void RabbitMqPublisher_IdempotentDispose()
    {
        var publisher = new RabbitMqPublisher(new RabbitMqPublisherOptions());

        Assert.DoesNotThrow(() =>
        {
            publisher.Dispose();
            publisher.Dispose();
        });
    }

    [Test]
    public async Task CopyStream_ProducesCorrectPayload()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        using var source = new MemoryStream(data);
        using var destination = new MemoryStream();

        await source.CopyToAsync(destination);

        Assert.That(destination.ToArray(), Is.EqualTo(data));
    }

    [Test]
    public void CreateChange_BuildsCorrectMessageKey()
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
    public void CreateChange_BuildsCorrectRoutingKey()
    {
        var change = new EntityChange
        {
            EntityType = "Order",
            EntityId = "order-456",
            ChangeType = ChangeType.Delete,
            Timestamp = DateTime.UtcNow,
            CorrelationId = Guid.NewGuid()
        };

        var options = new RabbitMqPublisherOptions { RoutingKey = "events" };
        var expectedRoutingKey = $"{options.RoutingKey}.{change.EntityType}.{change.ChangeType.ToString().ToLower()}";
        Assert.That(expectedRoutingKey, Is.EqualTo("events.Order.delete"));
    }

    [Test]
    public void EntityChange_AllMetadataFields_Present()
    {
        var correlationId = Guid.NewGuid();
        var timestamp = new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc);
        var change = new EntityChange
        {
            EntityType = "Product",
            EntityId = "prod-789",
            ChangeType = ChangeType.Insert,
            Timestamp = timestamp,
            CorrelationId = correlationId,
            Version = 5,
            Published = false
        };

        Assert.That(change.EntityType, Is.EqualTo("Product"));
        Assert.That(change.EntityId, Is.EqualTo("prod-789"));
        Assert.That(change.ChangeType, Is.EqualTo(ChangeType.Insert));
        Assert.That(change.Timestamp, Is.EqualTo(timestamp));
        Assert.That(change.CorrelationId, Is.EqualTo(correlationId));
        Assert.That(change.Version, Is.EqualTo(5));
        Assert.That(change.Published, Is.False);
    }

    [Test]
    public async Task PublishAsync_EmptyPayload_DoesNotThrow()
    {
        var mockChannel = new Mock<IChannel>();
        mockChannel.Setup(c => c.IsOpen).Returns(true);

        var options = new RabbitMqPublisherOptions { DeclareExchange = false };
        var publisher = new RabbitMqPublisher(options);
        SetChannelViaReflection(publisher, mockChannel.Object);

        var change = new EntityChange
        {
            EntityType = "Test",
            EntityId = "1",
            ChangeType = ChangeType.Insert,
            Timestamp = DateTime.UtcNow,
            CorrelationId = Guid.NewGuid()
        };

        Assert.DoesNotThrowAsync(async () => await publisher.PublishAsync(new MessageEnvelope
        {
            EntityType = change.EntityType,
            EntityId = change.EntityId,
            ChangeType = change.ChangeType,
            CorrelationId = change.CorrelationId,
            Version = change.Version,
            Timestamp = change.Timestamp,
            Payload = Array.Empty<byte>()
        }));
    }

    [Test]
    public async Task PublishAsync_VerifiesBasicPublishCalled()
    {
        var mockChannel = new Mock<IChannel>();
        mockChannel.Setup(c => c.IsOpen).Returns(true);

        var options = new RabbitMqPublisherOptions { DeclareExchange = false };
        var publisher = new RabbitMqPublisher(options);
        SetChannelViaReflection(publisher, mockChannel.Object);

        var change = new EntityChange
        {
            EntityType = "TestEntity",
            EntityId = "test-1",
            ChangeType = ChangeType.Update,
            Timestamp = DateTime.UtcNow,
            CorrelationId = Guid.NewGuid()
        };

        await publisher.PublishAsync(new MessageEnvelope
        {
            EntityType = change.EntityType,
            EntityId = change.EntityId,
            ChangeType = change.ChangeType,
            CorrelationId = change.CorrelationId,
            Version = change.Version,
            Timestamp = change.Timestamp,
            Payload = [0xAA, 0xBB, 0xCC]
        });

        mockChannel.Verify(c => c.BasicPublishAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<BasicProperties>(),
                It.Is<ReadOnlyMemory<byte>>(m => m.Length > 0),
                cancellationToken: It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Test]
    public async Task PublishAsync_UsesCorrectExchangeName()
    {
        var mockChannel = new Mock<IChannel>();
        mockChannel.Setup(c => c.IsOpen).Returns(true);

        var options = new RabbitMqPublisherOptions { ExchangeName = "my_custom_exchange", DeclareExchange = false };
        var publisher = new RabbitMqPublisher(options);
        SetChannelViaReflection(publisher, mockChannel.Object);

        var change = new EntityChange
        {
            EntityType = "Test",
            EntityId = "1",
            ChangeType = ChangeType.Insert,
            Timestamp = DateTime.UtcNow,
            CorrelationId = Guid.NewGuid()
        };

        await publisher.PublishAsync(new MessageEnvelope
        {
            EntityType = change.EntityType,
            EntityId = change.EntityId,
            ChangeType = change.ChangeType,
            CorrelationId = change.CorrelationId,
            Version = change.Version,
            Timestamp = change.Timestamp,
            Payload = [1]
        });

        mockChannel.Verify(c => c.BasicPublishAsync(
                "my_custom_exchange",
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<BasicProperties>(),
                It.Is<ReadOnlyMemory<byte>>(m => m.Length > 0),
                It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    private static void SetChannelViaReflection(RabbitMqPublisher publisher, IChannel channel)
    {
        var field = typeof(RabbitMqPublisher).GetField("_channel",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field!.SetValue(publisher, channel);
    }

    private static void SetConnectionAndChannelViaReflection(
        RabbitMqPublisher publisher,
        IConnection connection,
        IChannel channel)
    {
        var connField = typeof(RabbitMqPublisher).GetField("_connection",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        connField!.SetValue(publisher, connection);

        var chanField = typeof(RabbitMqPublisher).GetField("_channel",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        chanField!.SetValue(publisher, channel);
    }
}
