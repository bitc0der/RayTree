using RayTree.Core.Models;
using RayTree.Core.Plugins;
using RayTree.Core.Plugins.Compression;
using RayTree.Core.Plugins.Serialization;
using RayTree.Core.Tracking;
using RayTree.Plugins;
using RayTree.Plugins.Compressors.Gzip;
using RayTree.Plugins.Serializers.Json;

namespace RayTree.Core.Tests;

public class SerializationPipelineTests
{
    private class Customer
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }

    [Test]
    public async Task JsonSerializer_SerializeThenDeserialize_RoundTripsChange()
    {
        var serializer = new JsonSerializerPlugin();
        var change = new EntityChange<Customer>
        {
            Id = 1,
            EntityType = typeof(Customer).FullName!,
            EntityId = "e-123",
            ChangeType = ChangeType.Update,
            Version = 3,
            CorrelationId = Guid.NewGuid(),
            Published = false,
            State = new Customer { Id = 1, Name = "Acme" }
        };

        using var ms = new MemoryStream();
        await serializer.SerializeAsync(change, ms);
        ms.Position = 0;

        var result = await serializer.DeserializeAsync<Customer>(ms);

        Assert.That(result.EntityType,    Is.EqualTo(change.EntityType));
        Assert.That(result.EntityId,      Is.EqualTo(change.EntityId));
        Assert.That(result.ChangeType,    Is.EqualTo(change.ChangeType));
        Assert.That(result.Version,       Is.EqualTo(change.Version));
        Assert.That(result.CorrelationId, Is.EqualTo(change.CorrelationId));
        Assert.That(result.Published,     Is.EqualTo(change.Published));
        Assert.That(result.State?.Name,   Is.EqualTo("Acme"));
    }

    [Test]
    public void JsonSerializer_Name_ReturnsJson()
    {
        var serializer = new JsonSerializerPlugin();
        Assert.That(serializer.Name, Is.EqualTo("Json"));
    }
}

public class CompressionPipelineTests
{
    private class Item
    {
        public int Id { get; set; }
        public string? Label { get; set; }
    }

    [Test]
    public void GzipCompressor_Name_ReturnsGzip()
    {
        var compressor = new GzipCompressorPlugin();
        Assert.That(compressor.Name, Is.EqualTo("Gzip"));
    }

    [Test]
    public async Task NoOpCompressor_PassThroughData_Unchanged()
    {
        var compressor = new NoOpCompressorPlugin();
        var originalData = new byte[] { 1, 2, 3, 4, 5 };

        using var source = new MemoryStream(originalData);
        using var output = new MemoryStream();
        await compressor.CompressAsync(source, output);

        Assert.That(output.ToArray(), Is.EqualTo(originalData));
    }

    [Test]
    public void NoOpCompressor_Name_ReturnsNoOp()
    {
        var compressor = new NoOpCompressorPlugin();
        Assert.That(compressor.Name, Is.EqualTo("NoOp"));
    }

    [Test]
    public async Task FullPipeline_SerializeCompressDecompressDeserialize_RoundTripsChange()
    {
        var serializer = new JsonSerializerPlugin();
        var compressor = new GzipCompressorPlugin();
        var change = new EntityChange<Item>
        {
            Id = 42,
            EntityType = typeof(Item).FullName!,
            EntityId = "item-001",
            ChangeType = ChangeType.Insert,
            Version = 1,
            CorrelationId = Guid.NewGuid(),
            State = new Item { Id = 42, Label = "Widget" }
        };

        using var serialized = new MemoryStream();
        await serializer.SerializeAsync(change, serialized);
        serialized.Position = 0;

        using var compressed = new MemoryStream();
        await compressor.CompressAsync(serialized, compressed);
        compressed.Position = 0;

        using var decompressed = new MemoryStream();
        await compressor.DecompressAsync(compressed, decompressed);
        decompressed.Position = 0;

        var deserialized = await serializer.DeserializeAsync<Item>(decompressed);

        Assert.That(deserialized.EntityType, Is.EqualTo(change.EntityType));
        Assert.That(deserialized.EntityId,   Is.EqualTo(change.EntityId));
        Assert.That(deserialized.ChangeType, Is.EqualTo(change.ChangeType));
        Assert.That(deserialized.Version,    Is.EqualTo(change.Version));
        Assert.That(deserialized.State?.Label, Is.EqualTo("Widget"));
    }
}
