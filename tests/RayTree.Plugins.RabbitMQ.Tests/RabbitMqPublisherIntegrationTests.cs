using System.IO.Pipelines;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RayTree.Models;
using RayTree.Tracking;
using Testcontainers.RabbitMq;

namespace RayTree.Plugins.RabbitMQ.Tests;

[NonParallelizable]
public class RabbitMqPublisherIntegrationTests : IAsyncDisposable
{
    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder()
        .WithImage("rabbitmq:3.13-management-alpine")
        .Build();

    private string _hostName = null!;
    private int _port;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        await _rabbitMq.StartAsync();
        _hostName = _rabbitMq.Hostname;
        _port = _rabbitMq.GetMappedPublicPort(5672);
    }

    public ValueTask DisposeAsync() => _rabbitMq.DisposeAsync();

    [Test]
    public async Task PublishAsync_SendsMessageToExchange()
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
            EntityId = "test-123",
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
        Assert.That(headers!["entity_type"], Is.EqualTo("TestEntity"));
        Assert.That(headers["entity_id"], Is.EqualTo("test-123"));
        Assert.That(headers["change_type"], Is.EqualTo("Update"));
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
                EntityId = $"msg-{i}",
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
            var msg = await ConsumeMessageAsync(TimeSpan.FromSeconds(10));
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

    private RabbitMqPublisher CreatePublisher()
    {
        return new RabbitMqPublisher(new RabbitMqPublisherOptions
        {
            HostName = _hostName,
            Port = _port,
            UserName = "guest",
            Password = "guest",
            ExchangeName = "test_entity_changes",
            RoutingKey = "test",
            DeclareExchange = true,
            ExchangeType = "topic",
            Durable = false
        });
    }

    private async Task<EntityChange?> ConsumeMessageAsync(TimeSpan? timeout = null)
    {
        var (change, _) = await ConsumeMessageWithHeadersAsync(timeout);
        return change;
    }

    private async Task<(EntityChange? Change, IDictionary<string, object?>? Headers)> ConsumeMessageWithHeadersAsync(TimeSpan? timeout = null)
    {
        var tcs = new TaskCompletionSource<(EntityChange? Change, IDictionary<string, object?>? Headers)>();
        timeout ??= TimeSpan.FromSeconds(5);

        var factory = new ConnectionFactory
        {
            HostName = _hostName,
            Port = _port,
            UserName = "guest",
            Password = "guest"
        };

        using var conn = factory.CreateConnection();
        using var channel = conn.CreateModel();

        var queueName = channel.QueueDeclare().QueueName;
        channel.QueueBind(queueName, "test_entity_changes", "test.#");

        var consumer = new EventingBasicConsumer(channel);
        consumer.Received += (_, ea) =>
        {
            var entityType = ea.BasicProperties.Headers["entity_type"]?.ToString() ?? "Unknown";
            var entityId = ea.BasicProperties.Headers["entity_id"]?.ToString() ?? "0";
            var changeTypeStr = ea.BasicProperties.Headers["change_type"]?.ToString() ?? "Insert";

            var change = new EntityChange
            {
                EntityType = entityType,
                EntityId = entityId,
                ChangeType = Enum.Parse<ChangeType>(changeTypeStr)
            };

            tcs.TrySetResult((change, ea.BasicProperties.Headers));
        };

        channel.BasicConsume(queueName, true, consumer);

        using var cts = new CancellationTokenSource(timeout.Value);
        cts.Token.Register(() => tcs.TrySetResult((null, null)));

        return await tcs.Task;
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
