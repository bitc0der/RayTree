using RayTree.Plugins;
using RayTree.Plugins.InMemory;

namespace RayTree.Core.Tests;

public class AssemblyLoadingTests
{
    [Test]
    public void InMemoryRepository_CanBeInstantiated()
    {
        var repo = new InMemoryRepository<TestEntity>();

        Assert.That(repo, Is.Not.Null);
        Assert.That(repo, Is.InstanceOf<IRepository<TestEntity>>());
    }

    [Test]
    public void InMemoryOutbox_CanBeInstantiated()
    {
        var outbox = new InMemoryOutbox();

        Assert.That(outbox, Is.Not.Null);
        Assert.That(outbox, Is.InstanceOf<IOutbox>());
    }

    [Test]
    public void InMemoryQueue_CanBeInstantiated()
    {
        var queue = new InMemoryQueue();

        Assert.That(queue, Is.Not.Null);
        Assert.That(queue, Is.InstanceOf<IQueuePublisher>());
    }

    [Test]
    public void NoOpCompressor_CanBeInstantiated()
    {
        var compressor = new NoOpCompressorPlugin();

        Assert.That(compressor, Is.Not.Null);
        Assert.That(compressor, Is.InstanceOf<IChangeCompressor>());
        Assert.That(compressor.Name, Is.EqualTo("NoOp"));
    }

    [Test]
    public void JsonSerializers_Assembly_CanBeLoaded()
    {
        var assembly = typeof(RayTree.Plugins.Serializers.Json.JsonSerializerPlugin).Assembly;

        Assert.That(assembly, Is.Not.Null);
        Assert.That(assembly.GetName().Name, Does.Contain("Serializers.Json"));
    }

    [Test]
    public void ProtobufSerializers_Assembly_CanBeLoaded()
    {
        var assembly = typeof(RayTree.Plugins.Serializers.Protobuf.ProtobufSerializerPlugin).Assembly;

        Assert.That(assembly, Is.Not.Null);
        Assert.That(assembly.GetName().Name, Does.Contain("Serializers.Protobuf"));
    }

    [Test]
    public void MessagePackSerializers_Assembly_CanBeLoaded()
    {
        var assembly = typeof(RayTree.Plugins.Serializers.MessagePack.MessagePackSerializerPlugin).Assembly;

        Assert.That(assembly, Is.Not.Null);
        Assert.That(assembly.GetName().Name, Does.Contain("Serializers.MessagePack"));
    }

    [Test]
    public void GzipCompressors_Assembly_CanBeLoaded()
    {
        var assembly = typeof(RayTree.Plugins.Compressors.Gzip.GzipCompressorPlugin).Assembly;

        Assert.That(assembly, Is.Not.Null);
        Assert.That(assembly.GetName().Name, Does.Contain("Compressors.Gzip"));
    }

    [Test]
    public void BrotliCompressors_Assembly_CanBeLoaded()
    {
        var assembly = typeof(RayTree.Plugins.Compressors.Brotli.BrotliCompressorPlugin).Assembly;

        Assert.That(assembly, Is.Not.Null);
        Assert.That(assembly.GetName().Name, Does.Contain("Compressors.Brotli"));
    }

    [Test]
    public void Lz4Compressors_Assembly_CanBeLoaded()
    {
        var assembly = typeof(RayTree.Plugins.Compressors.Lz4.Lz4CompressorPlugin).Assembly;

        Assert.That(assembly, Is.Not.Null);
        Assert.That(assembly.GetName().Name, Does.Contain("Compressors.Lz4"));
    }

    [Test]
    public void InMemory_Assembly_CanBeLoaded()
    {
        var assembly = typeof(InMemoryRepository<object>).Assembly;

        Assert.That(assembly, Is.Not.Null);
        Assert.That(assembly.GetName().Name, Does.Contain("InMemory"));
    }
}

public class TestEntity
{
    public int Id { get; set; }
    public string? Name { get; set; }
}
