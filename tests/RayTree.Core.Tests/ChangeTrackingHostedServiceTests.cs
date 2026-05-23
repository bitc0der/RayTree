using Microsoft.Extensions.Logging.Abstractions;
using RayTree.Core.Models;
using RayTree.Core.Plugins.Compression;
using RayTree.Core.Tracking;
using RayTree.Hosting;
using RayTree.Plugins.InMemory;
using RayTree.Plugins.Serializers.Json;

namespace RayTree.Core.Tests;

/// <summary>
/// Tests for <see cref="ChangeTrackingHostedService"/> isolated-mode startup path.
/// Verifies that <see cref="ChangeTrackingHostedService.StartAsync"/> starts exactly one
/// consume loop per <c>(entity type, handler name)</c> pair registered in Isolated mode,
/// and that each loop dispatches only to its own named handler.
/// </summary>
[TestFixture]
public class ChangeTrackingHostedServiceTests
{
    private class Order { public int Id { get; set; } }

    /// <summary>
    /// Serializes an <see cref="EntityChange{TEntity}"/> via the JSON serializer so that
    /// isolated consumer loops can deserialize it successfully.
    /// </summary>
    private static async Task<byte[]> SerializeAsync(Order entity, ChangeType changeType)
    {
        var change = new EntityChange<Order>
        {
            EntityType    = typeof(Order).AssemblyQualifiedName!,
            EntityId      = entity.Id.ToString(),
            ChangeType    = changeType,
            CorrelationId = Guid.NewGuid(),
            State         = entity,
            Timestamp     = DateTime.UtcNow,
        };
        using var ms = new System.IO.MemoryStream();
        await new JsonSerializerPlugin().SerializeAsync(change, ms, CancellationToken.None);
        return ms.ToArray();
    }

    // -------------------------------------------------------------------------
    // Test: two isolated handler names → two independent loops started; each
    // loop dispatches only its own handler.
    // -------------------------------------------------------------------------

    [Test]
    public async Task StartAsync_IsolatedMode_StartsOneLoopPerHandlerName()
    {
        var consumers      = new Dictionary<string, InMemoryQueue>(StringComparer.Ordinal);
        var readModelCount = 0;
        var notifierCount  = 0;

        using var tracker = new ChangeTrackingBuilder(NullLoggerFactory.Instance)
            .ForEntity<Order>(e =>
                e.UseOutbox(new InMemoryOutbox())
                 .UsePublisher(new InMemoryQueue())
                 .UseSerializer(new JsonSerializerPlugin())
                 .UseCompressor(new NoOpCompressorPlugin())
                 .UseConsumerFactory(name =>
                 {
                     var q = new InMemoryQueue();
                     consumers[name] = q;
                     return q;
                 })
                 .OnChange("read-model", ChangeType.Insert, (_, _) =>
                 {
                     Interlocked.Increment(ref readModelCount);
                     return Task.CompletedTask;
                 })
                 .OnChange("notifier", ChangeType.Insert, (_, _) =>
                 {
                     Interlocked.Increment(ref notifierCount);
                     return Task.CompletedTask;
                 }))
            .Build();

        // Factory is invoked once per distinct handler name at Build() time.
        Assert.That(consumers.Keys, Is.EquivalentTo(new[] { "read-model", "notifier" }),
            "factory must be called once per distinct handler name at Build()");

        var svc = new ChangeTrackingHostedService(tracker, NullLogger<ChangeTrackingHostedService>.Instance);

        await svc.StartAsync(CancellationToken.None);

        // Build a properly serialized envelope and publish it to the read-model consumer.
        var payload = await SerializeAsync(new Order { Id = 1 }, ChangeType.Insert);
        var envelope = new MessageEnvelope
        {
            EntityType    = typeof(Order).AssemblyQualifiedName!,
            EntityId      = "1",
            ChangeType    = ChangeType.Insert,
            CorrelationId = Guid.NewGuid(),
            Payload       = payload,
        };
        await consumers["read-model"].PublishAsync(envelope);

        // Wait up to 3 s for the isolated loop to process the message.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (Volatile.Read(ref readModelCount) == 0 && !cts.IsCancellationRequested)
            await Task.Delay(10, cts.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        Assert.That(readModelCount, Is.EqualTo(1),
            "read-model loop must dispatch exactly one message");
        Assert.That(notifierCount, Is.EqualTo(0),
            "notifier loop must not receive messages from read-model's consumer");

        await svc.StopAsync(CancellationToken.None);
    }

    // -------------------------------------------------------------------------
    // Test: publisher-only tracker (no subscriber) → StartAsync is a no-op
    // -------------------------------------------------------------------------

    [Test]
    public async Task StartAsync_NoSubscriber_CompletesWithoutError()
    {
        using var tracker = new ChangeTrackingBuilder(NullLoggerFactory.Instance)
            .ForEntity<Order>(e =>
                e.UseOutbox(new InMemoryOutbox())
                 .UsePublisher(new InMemoryQueue())
                 .UseSerializer(new JsonSerializerPlugin())
                 .UseCompressor(new NoOpCompressorPlugin()))
            .Build();

        // No consumer registered → tracker has no subscriber → StartAsync must be a no-op
        var svc = new ChangeTrackingHostedService(tracker, NullLogger<ChangeTrackingHostedService>.Instance);

        Assert.DoesNotThrowAsync(() => svc.StartAsync(CancellationToken.None));
        await svc.StopAsync(CancellationToken.None);
    }
}
