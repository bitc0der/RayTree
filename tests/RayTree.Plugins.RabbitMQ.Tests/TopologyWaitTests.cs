using System.Diagnostics;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using Testcontainers.RabbitMq;

namespace RayTree.Plugins.RabbitMQ.Tests;

/// <summary>
/// Verifies the opt-in topology-wait behaviour for <see cref="RabbitMqPublisher"/> and
/// <see cref="RabbitMqConsumer"/>: when <c>WaitForTopology = true</c> the side that does NOT
/// own the topology (declare disabled) probes via passive declares and retries on
/// <c>NOT_FOUND</c> until the other side creates it.
/// </summary>
[NonParallelizable]
public class TopologyWaitTests : IAsyncDisposable
{
    private readonly RabbitMqContainer _rabbitMq = new RabbitMqBuilder("rabbitmq:4.3.0-alpine")
        .Build();

    [OneTimeSetUp]
    public Task OneTimeSetUp() => _rabbitMq.StartAsync();

    public ValueTask DisposeAsync() => _rabbitMq.DisposeAsync();

    private ConnectionFactory CreateFactory() => new()
    {
        HostName = _rabbitMq.Hostname,
        Port = _rabbitMq.GetMappedPublicPort(5672),
        UserName = RabbitMqBuilder.DefaultUsername,
        Password = RabbitMqBuilder.DefaultPassword
    };

    private RabbitMqPublisherOptions BasePublisherOptions(string exchangeName) => new()
    {
        HostName = _rabbitMq.Hostname,
        Port = _rabbitMq.GetMappedPublicPort(5672),
        UserName = RabbitMqBuilder.DefaultUsername,
        Password = RabbitMqBuilder.DefaultPassword,
        ExchangeName = exchangeName,
        ExchangeType = "topic",
        DeclareExchange = false
    };

    private RabbitMqConsumerOptions BaseConsumerOptions(string queueName, string? exchangeName = null) => new()
    {
        HostName = _rabbitMq.Hostname,
        Port = _rabbitMq.GetMappedPublicPort(5672),
        UserName = RabbitMqBuilder.DefaultUsername,
        Password = RabbitMqBuilder.DefaultPassword,
        QueueName = queueName,
        DeclareQueue = false,
        ExchangeName = exchangeName
    };

    // ---------------------------------------------------------------------
    // 5.2 Publisher waits for an externally-owned exchange to appear.
    // ---------------------------------------------------------------------
    [Test]
    public async Task Publisher_waits_then_succeeds_when_exchange_appears_late()
    {
        var exchangeName = $"late-exch-{Guid.NewGuid():N}";
        var options = BasePublisherOptions(exchangeName);
        options.WaitForTopology = true;
        options.TopologyWaitInterval = TimeSpan.FromMilliseconds(200);

        using var publisher = new RabbitMqPublisher(options);

        // Declare the exchange after a short delay on a separate connection.
        var declareTask = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            var factory = CreateFactory();
            await using var conn = await factory.CreateConnectionAsync();
            await using var ch = await conn.CreateChannelAsync();
            await ch.ExchangeDeclareAsync(exchangeName, type: "topic", durable: true);
        });

        await publisher.InitializeAsync();
        await declareTask;

        Assert.DoesNotThrowAsync(async () => await publisher.PublishAsync(new Core.Models.MessageEnvelope
        {
            EntityType = "Order",
            EntityId = "1",
            ChangeType = Core.Tracking.ChangeType.Insert,
            CorrelationId = Guid.NewGuid(),
            Timestamp = DateTime.UtcNow,
            Payload = new byte[] { 1 }
        }));
    }

    // ---------------------------------------------------------------------
    // 5.3 Consumer waits for an externally-owned queue (no exchange binding)
    //     and a message published to that queue flows through end-to-end.
    // ---------------------------------------------------------------------
    [Test]
    public async Task Consumer_waits_then_succeeds_when_queue_appears_late()
    {
        var queueName = $"late-queue-{Guid.NewGuid():N}";
        var options = BaseConsumerOptions(queueName);
        options.WaitForTopology = true;
        options.TopologyWaitInterval = TimeSpan.FromMilliseconds(200);

        using var consumer = new RabbitMqConsumer(options);

        var declareTask = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            var factory = CreateFactory();
            await using var conn = await factory.CreateConnectionAsync();
            await using var ch = await conn.CreateChannelAsync();
            await ch.QueueDeclareAsync(queueName, durable: true, exclusive: false, autoDelete: false);
        });

        await consumer.InitializeAsync();
        await declareTask;

        // Publish a message directly to the queue (default exchange + queueName routing key)
        // on a separate connection, then drain it from the consumer's IAsyncEnumerable to verify
        // the consumer is actually wired up — not just that InitializeAsync returned.
        var factory = CreateFactory();
        await using (var conn = await factory.CreateConnectionAsync())
        await using (var ch = await conn.CreateChannelAsync())
        {
            var props = new BasicProperties
            {
                MessageId = Guid.NewGuid().ToString(),
                Headers = new Dictionary<string, object?>
                {
                    ["entity_type"] = "Order",
                    ["entity_id"] = "1",
                    ["change_type"] = "Insert",
                    ["version"] = 0
                }
            };
            await ch.BasicPublishAsync(exchange: "", routingKey: queueName,
                mandatory: false, basicProperties: props, body: new byte[] { 1 });
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var enumerator = consumer.ConsumeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        Assert.That(await enumerator.MoveNextAsync(), Is.True, "expected a message to arrive within 10s");
        Assert.That(enumerator.Current.EntityType, Is.EqualTo("Order"));
        Assert.That(enumerator.Current.EntityId, Is.EqualTo("1"));
    }

    // ---------------------------------------------------------------------
    // 5.4 Consumer waits for an externally-owned exchange used for binding.
    // ---------------------------------------------------------------------
    [Test]
    public async Task Consumer_waits_then_succeeds_when_bound_exchange_appears_late()
    {
        var exchangeName = $"late-bind-exch-{Guid.NewGuid():N}";
        var queueName = $"q-{Guid.NewGuid():N}";

        var options = BaseConsumerOptions(queueName, exchangeName);
        options.DeclareQueue = true;   // consumer owns the queue, waits for the exchange
        options.WaitForTopology = true;
        options.TopologyWaitInterval = TimeSpan.FromMilliseconds(200);
        options.BindingKey = "#";

        using var consumer = new RabbitMqConsumer(options);

        var declareTask = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            var factory = CreateFactory();
            await using var conn = await factory.CreateConnectionAsync();
            await using var ch = await conn.CreateChannelAsync();
            await ch.ExchangeDeclareAsync(exchangeName, type: "topic", durable: true);
        });

        await consumer.InitializeAsync();
        await declareTask;
        Assert.Pass();
    }

    // ---------------------------------------------------------------------
    // 5.5 Timeout exhaustion surfaces the underlying NOT_FOUND.
    // ---------------------------------------------------------------------
    [Test]
    public void Timeout_exhaustion_throws_NotFound()
    {
        var options = BasePublisherOptions($"never-{Guid.NewGuid():N}");
        options.WaitForTopology = true;
        options.TopologyWaitInterval = TimeSpan.FromMilliseconds(100);
        options.TopologyWaitTimeout = TimeSpan.FromMilliseconds(500);

        using var publisher = new RabbitMqPublisher(options);

        var ex = Assert.ThrowsAsync<OperationInterruptedException>(
            async () => await publisher.InitializeAsync());
        Assert.That(ex!.ShutdownReason!.ReplyCode, Is.EqualTo((ushort)404));
    }

    // ---------------------------------------------------------------------
    // 5.6 Default (opt-out) behaviour is unchanged: throws immediately.
    //
    // Exercised on the consumer side because the publisher with `mandatory: false`
    // silently drops messages routed to a missing exchange; only the consumer's
    // BasicConsume against a missing queue surfaces NOT_FOUND eagerly.
    // ---------------------------------------------------------------------
    [Test]
    public void Default_options_still_throw_immediately()
    {
        var options = BaseConsumerOptions($"missing-{Guid.NewGuid():N}");
        // WaitForTopology default (false), DeclareQueue = false (BaseConsumerOptions default).
        using var consumer = new RabbitMqConsumer(options);

        var sw = Stopwatch.StartNew();
        var ex = Assert.ThrowsAsync<OperationInterruptedException>(
            async () => await consumer.InitializeAsync());
        sw.Stop();

        Assert.That(ex!.ShutdownReason!.ReplyCode, Is.EqualTo((ushort)404));
        Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)),
            "default behaviour must not retry on NOT_FOUND");
    }

    // ---------------------------------------------------------------------
    // 5.7 Non-NOT_FOUND errors do not retry.
    //
    // Note: the broker's PRECONDITION_FAILED is injected via the active-declare path because
    // RabbitMQ's passive declare can only return NOT_FOUND for missing topology (argument
    // mismatches are impossible — passive declare takes no arguments). The TopologyProbe's
    // exception filter (`ReplyCode == 404`) is identical for both paths, so any
    // OperationInterruptedException with a non-404 reply code propagates without retry. The
    // probe path's "no retry on non-404" is independently covered by
    // Cancellation_during_wait_throws_OperationCanceledException (an OperationCanceledException
    // is a non-NOT_FOUND throwable that exits the loop on the first occurrence).
    // ---------------------------------------------------------------------
    [Test]
    public async Task NonNotFound_error_does_not_retry()
    {
        // Pre-create an exchange with type "direct".
        var exchangeName = $"mismatch-{Guid.NewGuid():N}";
        var factory = CreateFactory();
        await using (var conn = await factory.CreateConnectionAsync())
        await using (var ch = await conn.CreateChannelAsync())
        {
            await ch.ExchangeDeclareAsync(exchangeName, type: "direct", durable: true);
        }

        // Now configure a publisher that wants to declare the same exchange as "topic" — that's
        // PRECONDITION_FAILED (406), not NOT_FOUND. WaitForTopology must not mask it.
        var options = BasePublisherOptions(exchangeName);
        options.WaitForTopology = true;
        options.TopologyWaitInterval = TimeSpan.FromMilliseconds(100);
        options.TopologyWaitTimeout = TimeSpan.FromSeconds(5);
        options.DeclareExchange = true;   // active declare — type mismatch surfaces immediately.
        options.ExchangeType = "topic";

        using var publisher = new RabbitMqPublisher(options);

        var sw = Stopwatch.StartNew();
        var ex = Assert.ThrowsAsync<OperationInterruptedException>(
            async () => await publisher.InitializeAsync());
        sw.Stop();

        Assert.That(ex!.ShutdownReason!.ReplyCode, Is.Not.EqualTo((ushort)404));
        Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)),
            "non-NOT_FOUND errors must propagate immediately, not be retried");
    }

    // ---------------------------------------------------------------------
    // 5.8 Cancellation during the wait throws OperationCanceledException.
    // ---------------------------------------------------------------------
    [Test]
    public void Cancellation_during_wait_throws_OperationCanceledException()
    {
        var options = BasePublisherOptions($"never-{Guid.NewGuid():N}");
        options.WaitForTopology = true;
        options.TopologyWaitInterval = TimeSpan.FromSeconds(30);

        using var publisher = new RabbitMqPublisher(options);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var sw = Stopwatch.StartNew();
        Assert.CatchAsync<OperationCanceledException>(
            async () => await publisher.InitializeAsync(cts.Token));
        sw.Stop();

        Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)),
            "cancellation must propagate promptly, not wait for the full interval");
    }
}
