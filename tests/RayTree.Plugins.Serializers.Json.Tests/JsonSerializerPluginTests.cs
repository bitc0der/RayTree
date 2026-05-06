using RayTree.Core.Models;
using RayTree.Core.Tracking;

namespace RayTree.Plugins.Serializers.Json.Tests;

public class JsonSerializerPluginTests
{
    [Test]
    public async Task SerializeThenDeserialize_RoundTrip_PreservesAllFields()
    {
        var plugin = new JsonSerializerPlugin();
        var original = CreateTestChange();

        var data = await SerializeAndCaptureAsync(plugin, original);
        var result = await DeserializeFromDataAsync<User>(plugin, data);

        Assert.That(result.EntityId, Is.EqualTo(original.EntityId));
        Assert.That(result.EntityType, Is.EqualTo(original.EntityType));
        Assert.That(result.ChangeType, Is.EqualTo(original.ChangeType));
        Assert.That(result.Timestamp, Is.EqualTo(original.Timestamp));
        Assert.That(result.Version, Is.EqualTo(original.Version));
        Assert.That(result.CorrelationId, Is.EqualTo(original.CorrelationId));
        Assert.That(result.State?.Name, Is.EqualTo(original.State?.Name));
    }

    [Test]
    public async Task SerializeAsync_InsertChangeType()
    {
        var plugin = new JsonSerializerPlugin();
        var change = new EntityChange<User>
        {
            EntityType = typeof(User).FullName!,
            EntityId = "1",
            ChangeType = ChangeType.Insert,
            Timestamp = DateTime.UtcNow,
            State = new User { Id = 1, Name = "Alice" }
        };

        var data = await SerializeAndCaptureAsync(plugin, change);
        var result = await DeserializeFromDataAsync<User>(plugin, data);
        Assert.That(result.ChangeType, Is.EqualTo(ChangeType.Insert));
    }

    [Test]
    public async Task SerializeAsync_UpdateChangeType()
    {
        var plugin = new JsonSerializerPlugin();
        var change = new EntityChange<User>
        {
            EntityType = typeof(User).FullName!,
            EntityId = "2",
            ChangeType = ChangeType.Update,
            Timestamp = DateTime.UtcNow,
            State = new User { Id = 2 }
        };

        var data = await SerializeAndCaptureAsync(plugin, change);
        var result = await DeserializeFromDataAsync<User>(plugin, data);
        Assert.That(result.ChangeType, Is.EqualTo(ChangeType.Update));
    }

    [Test]
    public async Task SerializeAsync_DeleteChangeType()
    {
        var plugin = new JsonSerializerPlugin();
        var change = new EntityChange<User>
        {
            EntityType = typeof(User).FullName!,
            EntityId = "3",
            ChangeType = ChangeType.Delete,
            Timestamp = DateTime.UtcNow,
            State = new User { Id = 3 }
        };

        var data = await SerializeAndCaptureAsync(plugin, change);
        var result = await DeserializeFromDataAsync<User>(plugin, data);
        Assert.That(result.ChangeType, Is.EqualTo(ChangeType.Delete));
    }

    [Test]
    public async Task SerializeAsync_WithCorrelationId_PreservesCorrelationId()
    {
        var plugin = new JsonSerializerPlugin();
        var correlationId = Guid.NewGuid();
        var change = new EntityChange<User>
        {
            EntityType = typeof(User).FullName!,
            EntityId = "4",
            ChangeType = ChangeType.Insert,
            Timestamp = DateTime.UtcNow,
            CorrelationId = correlationId,
            State = new User { Id = 4 }
        };

        var data = await SerializeAndCaptureAsync(plugin, change);
        var result = await DeserializeFromDataAsync<User>(plugin, data);
        Assert.That(result.CorrelationId, Is.EqualTo(correlationId));
    }

    [Test]
    public void Name_ReturnsJson()
    {
        var plugin = new JsonSerializerPlugin();
        Assert.That(plugin.Name, Is.EqualTo("Json"));
    }

    [Test]
    public void DeserializeAsync_EmptyData_Throws()
    {
        var plugin = new JsonSerializerPlugin();

        Assert.ThrowsAsync<System.Text.Json.JsonException>(async () =>
            await plugin.DeserializeAsync<User>(new MemoryStream()));
    }

    private static async Task<byte[]> SerializeAndCaptureAsync<TEntity>(JsonSerializerPlugin plugin, EntityChange<TEntity> change)
        where TEntity : class
    {
        using var ms = new MemoryStream();
        await plugin.SerializeAsync(change, ms);
        return ms.ToArray();
    }

    private static async Task<EntityChange<TEntity>> DeserializeFromDataAsync<TEntity>(JsonSerializerPlugin plugin, byte[] data)
        where TEntity : class
    {
        return await plugin.DeserializeAsync<TEntity>(new MemoryStream(data));
    }

    private static EntityChange<User> CreateTestChange() => new()
    {
        Id = 42,
        EntityType = typeof(User).FullName!,
        EntityId = "user-123",
        ChangeType = ChangeType.Update,
        Timestamp = DateTime.UtcNow,
        Version = 3,
        CorrelationId = Guid.NewGuid(),
        Published = false,
        State = new User { Id = 42, Name = "Test User" }
    };

    private class User
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }
}
