using System.IO.Pipelines;
using RayTree.Models;
using RayTree.Tracking;

namespace RayTree.Plugins.Serializers.Protobuf.Tests;

public class ProtobufSerializerPluginTests
{
    [Test]
    public async Task SerializeThenDeserialize_RoundTrip_PreservesAllFields()
    {
        var plugin = new ProtobufSerializerPlugin();
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
        var plugin = new ProtobufSerializerPlugin();
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
        var plugin = new ProtobufSerializerPlugin();
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
        var plugin = new ProtobufSerializerPlugin();
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
        var plugin = new ProtobufSerializerPlugin();
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
    public async Task SerializeAsync_WithPublishedFlag_PreservesPublishedFlag()
    {
        var plugin = new ProtobufSerializerPlugin();
        var change = new EntityChange
        {
            EntityType = "TestEntity",
            EntityId = "5",
            ChangeType = ChangeType.Insert,
            Timestamp = DateTime.UtcNow,
            Published = true
        };

        var data = await SerializeAndCaptureAsync(plugin, change);
        var result = await DeserializeFromDataAsync(plugin, data, "TestEntity");
        Assert.That(result.Published, Is.True);
    }

    [Test]
    public void Name_ReturnsProtobuf()
    {
        var plugin = new ProtobufSerializerPlugin();
        Assert.That(plugin.Name, Is.EqualTo("Protobuf"));
    }

    [Test]
    public async Task SerializeAsync_ProducesCompactBinary()
    {
        var plugin = new ProtobufSerializerPlugin();
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
            Id = 100,
            EntityType = typeof(User).FullName!,
            EntityId = "user-456",
            ChangeType = ChangeType.Update,
            Timestamp = DateTime.UtcNow,
            Version = 5,
            CorrelationId = Guid.NewGuid(),
            Published = false
        };
    }

    [Test]
    public async Task SerializeAsync_GenericTypedState_RoundTrip()
    {
        var plugin = new ProtobufSerializerPlugin();
        var original = new EntityChange<User>
        {
            EntityType = typeof(User).FullName!,
            EntityId = "user-7",
            ChangeType = ChangeType.Update,
            Version = 2,
            State = new User { Id = 7, Name = "Bob" }
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

        var result = await plugin.DeserializeAsync<User>(deserializePipe.Reader);

        Assert.That(result.EntityId, Is.EqualTo("user-7"));
        Assert.That(result.ChangeType, Is.EqualTo(ChangeType.Update));
        Assert.That(result.Version, Is.EqualTo(2));
        Assert.That(result.State, Is.Not.Null);
        Assert.That(result.State!.Id, Is.EqualTo(7));
        Assert.That(result.State.Name, Is.EqualTo("Bob"));
    }

    private class User
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }
}
