using System.IO.Pipelines;
using Confluent.Kafka;
using RayTree.Models;
using RayTree.Tracking;
using Testcontainers.Kafka;

namespace RayTree.Plugins.Kafka.Tests;

[NonParallelizable]
public class KafkaPublisherIntegrationTests : IAsyncDisposable
{
    private readonly KafkaContainer _kafka = new KafkaBuilder()
        .WithImage("confluentinc/cp-kafka:7.6.1")
        .Build();

    private string _bootstrapServers = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await _kafka.StartAsync();
        _bootstrapServers = _kafka.GetBootstrapAddress();
    }

    public ValueTask DisposeAsync() => _kafka.DisposeAsync();

    [Test]
    public async Task PublishAsync_SendsMessageToTopic()
    {
        var publisher = CreatePublisher();
        var change = CreateTestChange();
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(new byte[] { 1, 2, 3 });
        await pipe.Writer.FlushAsync();
        await pipe.Writer.CompleteAsync();

        await publisher.PublishAsync(change, pipe.Reader);

        var received = await ConsumeMessageAsync();
        Assert.That(received, Is.Not.Null);
        Assert.That(received!.EntityId, Is.EqualTo(change.EntityId));
        Assert.That(received.ChangeType, Is.EqualTo(change.ChangeType));
    }

    [Test]
    public async Task PublishAsync_WithHeaders_ContainsEntityMetadata()
    {
        var publisher = CreatePublisher();
        var correlationId = Guid.NewGuid();
        var change = new EntityChange
        {
            EntityType = "TestEntity",
            EntityId = "kafka-123",
            ChangeType = ChangeType.Update,
            Timestamp = DateTime.UtcNow,
            CorrelationId = correlationId,
            Version = 5
        };
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(new byte[] { 4, 5, 6 });
        await pipe.Writer.FlushAsync();
        await pipe.Writer.CompleteAsync();

        await publisher.PublishAsync(change, pipe.Reader);

        var (received, headers) = await ConsumeMessageWithHeadersAsync();
        Assert.That(received, Is.Not.Null);
        Assert.That(received!.EntityType, Is.EqualTo("TestEntity"));
        Assert.That(received.EntityId, Is.EqualTo("kafka-123"));
    }

    [Test]
    public async Task PublishAsync_MultipleMessages_AllReceived()
    {
        var publisher = CreatePublisher();
        var receivedMessages = new List<EntityChange>();

        for (int i = 0; i < 3; i++)
        {
            var change = new EntityChange
            {
                EntityType = "TestEntity",
                EntityId = $"kafka-msg-{i}",
                ChangeType = ChangeType.Insert,
                Timestamp = DateTime.UtcNow
            };
            var pipe = new Pipe();
            await pipe.Writer.WriteAsync(new byte[] { (byte)i });
            await pipe.Writer.FlushAsync();
            await pipe.Writer.CompleteAsync();

            await publisher.PublishAsync(change, pipe.Reader);
        }

        for (int i = 0; i < 3; i++)
        {
            var msg = await ConsumeMessageAsync(TimeSpan.FromSeconds(30));
            Assert.That(msg, Is.Not.Null);
            receivedMessages.Add(msg!);
        }

        Assert.That(receivedMessages, Has.Count.EqualTo(3));
    }

    [Test]
    public void PublishAsync_WithDefaultOptions_ConnectsSuccessfully()
    {
        var publisher = CreatePublisher();
        Assert.That(publisher, Is.Not.Null);
    }

    private KafkaPublisher CreatePublisher()
    {
        return new KafkaPublisher(new KafkaPublisherOptions
        {
            BootstrapServers = _bootstrapServers,
            Topic = "test_entity_changes",
            Acks = "all"
        });
    }

    private async Task<EntityChange?> ConsumeMessageAsync(TimeSpan? timeout = null)
    {
        var (change, _) = await ConsumeMessageWithHeadersAsync(timeout);
        return change;
    }

    private async Task<(EntityChange? Change, Headers? Headers)> ConsumeMessageWithHeadersAsync(TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(30);
        var consumerConfig = new ConsumerConfig
        {
            BootstrapServers = _bootstrapServers,
            GroupId = "test-consumer-group",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true
        };

        using var consumer = new ConsumerBuilder<string, byte[]>(consumerConfig).Build();
        consumer.Subscribe("test_entity_changes");

        var result = consumer.Consume(timeout.Value);

        if (result == null)
            return (null, null);

        var entityType = result.Message.Headers?.TryGetLastBytes("entity_type", out var etBytes) == true
            ? System.Text.Encoding.UTF8.GetString(etBytes)
            : "Unknown";
        var entityId = result.Message.Headers?.TryGetLastBytes("entity_id", out var eiBytes) == true
            ? System.Text.Encoding.UTF8.GetString(eiBytes)
            : "0";
        var changeTypeStr = result.Message.Headers?.TryGetLastBytes("change_type", out var ctBytes) == true
            ? System.Text.Encoding.UTF8.GetString(ctBytes)
            : "Insert";

        var change = new EntityChange
        {
            EntityType = entityType,
            EntityId = entityId,
            ChangeType = Enum.Parse<ChangeType>(changeTypeStr)
        };

        return (change, result.Message.Headers);
    }

    private static EntityChange CreateTestChange()
    {
        return new EntityChange
        {
            EntityType = "TestEntity",
            EntityId = Guid.NewGuid().ToString(),
            ChangeType = ChangeType.Insert,
            Timestamp = DateTime.UtcNow,
            CorrelationId = Guid.NewGuid()
        };
    }
}
