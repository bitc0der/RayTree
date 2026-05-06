using System.IO.Pipelines;
using RayTree.Models;
using RayTree.Tracking;

namespace RayTree.Plugins.Serializers.MessagePack.Tests;

public class MessagePackSerializerPluginTests
{
    [Test]
    public async Task SerializeThenDeserialize_RoundTrip_PreservesAllFields()
    {
        var plugin = new MessagePackSerializerPlugin();
        var original = CreateTestChange();

        var data = await SerializeAndCaptureAsync(plugin, original);
        var result = await DeserializeFromDataAsync(plugin, data, "TestEntity");

        Assert.That(result.EntityId, Is.EqualTo(original.EntityId));
        Assert.That(result.EntityType, Is.EqualTo(original.EntityType));
        Assert.That(result.ChangeType, Is.EqualTo(original.ChangeType));
        Assert.That(result.Timestamp, Is.EqualTo(original.Timestamp).Within(1).Seconds);
        Assert.That(result.Version, Is.EqualTo(original.Version));
        Assert.That(result.CorrelationId, Is.EqualTo(original.CorrelationId));
        Assert.That(result.Published, Is.EqualTo(original.Published));
    }

    [Test]
    public async Task SerializeAsync_InsertChangeType()
    {
        var plugin = new MessagePackSerializerPlugin();
        var change = new EntityChange
        {
            EntityType = "TestEntity",
            EntityId = "1",
            ChangeType = ChangeType.Insert,
            Timestamp = DateTime.UtcNow
        };

        var data = await SerializeAndCaptureAsync(plugin, change);
        var result = await DeserializeFromDataAsync(plugin, data, "TestEntity");
        Assert.That(result.ChangeType, Is.EqualTo(ChangeType.Insert));
    }

    [Test]
    public async Task SerializeAsync_UpdateChangeType()
    {
        var plugin = new MessagePackSerializerPlugin();
        var change = new EntityChange
        {
            EntityType = "TestEntity",
            EntityId = "2",
            ChangeType = ChangeType.Update,
            Timestamp = DateTime.UtcNow
        };

        var data = await SerializeAndCaptureAsync(plugin, change);
        var result = await DeserializeFromDataAsync(plugin, data, "TestEntity");
        Assert.That(result.ChangeType, Is.EqualTo(ChangeType.Update));
    }

    [Test]
    public async Task SerializeAsync_DeleteChangeType()
    {
        var plugin = new MessagePackSerializerPlugin();
        var change = new EntityChange
        {
            EntityType = "TestEntity",
            EntityId = "3",
            ChangeType = ChangeType.Delete,
            Timestamp = DateTime.UtcNow
        };

        var data = await SerializeAndCaptureAsync(plugin, change);
        var result = await DeserializeFromDataAsync(plugin, data, "TestEntity");
        Assert.That(result.ChangeType, Is.EqualTo(ChangeType.Delete));
    }

    [Test]
    public async Task SerializeAsync_WithCorrelationId_PreservesCorrelationId()
    {
        var plugin = new MessagePackSerializerPlugin();
        var correlationId = Guid.NewGuid();
        var change = new EntityChange
        {
            EntityType = "TestEntity",
            EntityId = "4",
            ChangeType = ChangeType.Insert,
            Timestamp = DateTime.UtcNow,
            CorrelationId = correlationId
        };

        var data = await SerializeAndCaptureAsync(plugin, change);
        var result = await DeserializeFromDataAsync(plugin, data, "TestEntity");
        Assert.That(result.CorrelationId, Is.EqualTo(correlationId));
    }

    [Test]
    public void Name_ReturnsMessagePack()
    {
        var plugin = new MessagePackSerializerPlugin();
        Assert.That(plugin.Name, Is.EqualTo("MessagePack"));
    }

    [Test]
    public async Task SerializeAsync_ProducesCompactBinary()
    {
        var plugin = new MessagePackSerializerPlugin();
        var change = CreateTestChange();

        var data = await SerializeAndCaptureAsync(plugin, change);
        Assert.That(data.Length, Is.GreaterThan(0));
    }

    private static async Task<byte[]> SerializeAndCaptureAsync(IChangeSerializer plugin, EntityChange change)
    {
        var pipe = new Pipe();
        await plugin.SerializeAsync(change, pipe.Writer);
        return await ReadPipeDataAsync(pipe.Reader);
    }

    private static async Task<EntityChange> DeserializeFromDataAsync(IChangeSerializer plugin, byte[] data, string entityType)
    {
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(data);
        await pipe.Writer.FlushAsync();
        await pipe.Writer.CompleteAsync();
        return await plugin.DeserializeAsync(pipe.Reader, entityType);
    }

    private static async Task<byte[]> ReadPipeDataAsync(PipeReader reader)
    {
        using var ms = new MemoryStream();
        var result = await reader.ReadAsync();
        foreach (var segment in result.Buffer)
        {
            await ms.WriteAsync(segment);
        }
        reader.AdvanceTo(result.Buffer.End);
        await reader.CompleteAsync();
        return ms.ToArray();
    }

    private static EntityChange CreateTestChange()
    {
        return new EntityChange
        {
            Id = 200,
            EntityType = typeof(Order).FullName!,
            EntityId = "order-789",
            ChangeType = ChangeType.Insert,
            Timestamp = DateTime.UtcNow,
            Version = 1,
            CorrelationId = Guid.NewGuid(),
            Published = false
        };
    }

    [Test]
    public async Task SerializeAsync_GenericTypedState_RoundTrip()
    {
        var plugin = new MessagePackSerializerPlugin();
        var original = new EntityChange<Order>
        {
            EntityType = typeof(Order).FullName!,
            EntityId = "order-42",
            ChangeType = ChangeType.Insert,
            State = new Order { Id = 42, Total = 199.99m }
        };

        var pipe = new Pipe();
        await plugin.SerializeAsync(original, pipe.Writer);

        var dataMs = new MemoryStream();
        var readResult = await pipe.Reader.ReadAsync();
        foreach (var segment in readResult.Buffer)
        {
            await dataMs.WriteAsync(segment);
        }
        pipe.Reader.AdvanceTo(readResult.Buffer.End);
        await pipe.Reader.CompleteAsync();

        var dataBytes = dataMs.ToArray();
        var deserializePipe = new Pipe();
        await deserializePipe.Writer.WriteAsync(dataBytes);
        await deserializePipe.Writer.FlushAsync();
        await deserializePipe.Writer.CompleteAsync();

        var result = await plugin.DeserializeAsync<Order>(deserializePipe.Reader);

        Assert.That(result.EntityId, Is.EqualTo("order-42"));
        Assert.That(result.ChangeType, Is.EqualTo(ChangeType.Insert));
        Assert.That(result.State, Is.Not.Null);
        Assert.That(result.State!.Id, Is.EqualTo(42));
        Assert.That(result.State.Total, Is.EqualTo(199.99m));
    }

    public class Order
    {
        public int Id { get; set; }
        public decimal Total { get; set; }
    }
}
