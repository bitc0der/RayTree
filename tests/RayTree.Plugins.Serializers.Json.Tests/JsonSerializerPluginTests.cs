using System.IO.Pipelines;
using RayTree.Models;
using RayTree.Tracking;

namespace RayTree.Plugins.Serializers.Json.Tests;

public class JsonSerializerPluginTests
{
    [Test]
    public async Task SerializeThenDeserialize_RoundTrip_PreservesAllFields()
    {
        var plugin = new JsonSerializerPlugin();
        var original = CreateTestChange();

        var data = await SerializeAndCaptureAsync(plugin, original);
        var result = await DeserializeFromDataAsync(plugin, data, "TestEntity");

        Assert.That(result.EntityId, Is.EqualTo(original.EntityId));
        Assert.That(result.EntityType, Is.EqualTo(original.EntityType));
        Assert.That(result.ChangeType, Is.EqualTo(original.ChangeType));
        Assert.That(result.Timestamp, Is.EqualTo(original.Timestamp));
        Assert.That(result.Version, Is.EqualTo(original.Version));
        Assert.That(result.CorrelationId, Is.EqualTo(original.CorrelationId));
    }

    [Test]
    public async Task SerializeAsync_InsertChangeType()
    {
        var plugin = new JsonSerializerPlugin();
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
        var plugin = new JsonSerializerPlugin();
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
        var plugin = new JsonSerializerPlugin();
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
        var plugin = new JsonSerializerPlugin();
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
    public void Name_ReturnsJson()
    {
        var plugin = new JsonSerializerPlugin();
        Assert.That(plugin.Name, Is.EqualTo("Json"));
    }

    [Test]
    public async Task DeserializeAsync_EmptyData_Throws()
    {
        var plugin = new JsonSerializerPlugin();
        var pipe = new Pipe();
        await pipe.Writer.CompleteAsync();

        Assert.ThrowsAsync<System.Text.Json.JsonException>(async () =>
            await plugin.DeserializeAsync(pipe.Reader, "TestEntity"));
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
            Id = 42,
            EntityType = typeof(User).FullName!,
            EntityId = "user-123",
            ChangeType = ChangeType.Update,
            Timestamp = DateTime.UtcNow,
            Version = 3,
            CorrelationId = Guid.NewGuid(),
            Published = false
        };
    }

    private class User
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }
}
