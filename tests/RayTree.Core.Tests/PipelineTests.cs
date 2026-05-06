using System.IO.Pipelines;
using RayTree.Models;
using RayTree.Plugins;
using RayTree.Plugins.Compressors.Gzip;
using RayTree.Plugins.Serializers.Json;
using RayTree.Tracking;

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

        var pipe = new Pipe();
        await serializer.SerializeAsync(change, pipe.Writer);

        var result = await serializer.DeserializeAsync<Customer>(pipe.Reader);

        Assert.That(result.EntityType, Is.EqualTo(change.EntityType));
        Assert.That(result.EntityId, Is.EqualTo(change.EntityId));
        Assert.That(result.ChangeType, Is.EqualTo(change.ChangeType));
        Assert.That(result.Version, Is.EqualTo(change.Version));
        Assert.That(result.CorrelationId, Is.EqualTo(change.CorrelationId));
        Assert.That(result.Published, Is.EqualTo(change.Published));
        Assert.That(result.State?.Name, Is.EqualTo("Acme"));
    }

    [Test]
    public async Task JsonSerializer_Name_ReturnsJson()
    {
        var serializer = new JsonSerializerPlugin();

        var name = serializer.Name;

        Assert.That(name, Is.EqualTo("Json"));
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
    public async Task GzipCompressor_Name_ReturnsGzip()
    {
        var compressor = new GzipCompressorPlugin();

        var name = compressor.Name;

        Assert.That(name, Is.EqualTo("Gzip"));
    }

    [Test]
    public async Task NoOpCompressor_PassThroughData_Unchanged()
    {
        var compressor = new NoOpCompressorPlugin();
        var originalData = new byte[] { 1, 2, 3, 4, 5 };

        var sourcePipe = new Pipe();
        await sourcePipe.Writer.WriteAsync(originalData);
        sourcePipe.Writer.Complete();

        var outputPipe = new Pipe();

        await compressor.CompressAsync(sourcePipe.Reader, outputPipe.Writer);

        var compressedData = await ReadAllFromPipeAsync(outputPipe.Reader);

        Assert.That(compressedData, Is.EqualTo(originalData));
    }

    [Test]
    public async Task NoOpCompressor_Name_ReturnsNoOp()
    {
        var compressor = new NoOpCompressorPlugin();

        var name = compressor.Name;

        Assert.That(name, Is.EqualTo("NoOp"));
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

        var serializedBytes = await SerializeToBytesAsync(serializer, change);
        var compressOutput = await CompressDataAsync(compressor, serializedBytes);
        var decompressOutput = await DecompressDataAsync(compressor, compressOutput);
        var deserialized = await DeserializeFromBytesAsync<Item>(serializer, decompressOutput);

        Assert.That(deserialized.EntityType, Is.EqualTo(change.EntityType));
        Assert.That(deserialized.EntityId, Is.EqualTo(change.EntityId));
        Assert.That(deserialized.ChangeType, Is.EqualTo(change.ChangeType));
        Assert.That(deserialized.Version, Is.EqualTo(change.Version));
        Assert.That(deserialized.State?.Label, Is.EqualTo("Widget"));
    }

    private static async Task<byte[]> SerializeToBytesAsync<TEntity>(
        IChangeSerializer serializer,
        EntityChange<TEntity> change)
        where TEntity : class
    {
        using var ms = new MemoryStream();
        var writer = PipeWriter.Create(ms);
        await serializer.SerializeAsync(change, writer);
        return ms.ToArray();
    }

    private static async Task<EntityChange<TEntity>> DeserializeFromBytesAsync<TEntity>(
        IChangeSerializer serializer,
        byte[] data)
        where TEntity : class
    {
        var reader = PipeReader.Create(new MemoryStream(data));
        return await serializer.DeserializeAsync<TEntity>(reader);
    }

    private static async Task<byte[]> CompressDataAsync(IChangeCompressor compressor, byte[] data)
    {
        var sourcePipe = new Pipe();
        var outputPipe = new Pipe();

        await sourcePipe.Writer.WriteAsync(data);
        await sourcePipe.Writer.CompleteAsync();

        await compressor.CompressAsync(sourcePipe.Reader, outputPipe.Writer);

        return await ReadAllFromPipeAsync(outputPipe.Reader);
    }

    private static async Task<byte[]> DecompressDataAsync(IChangeCompressor compressor, byte[] data)
    {
        var sourcePipe = new Pipe();
        var outputPipe = new Pipe();

        await sourcePipe.Writer.WriteAsync(data);
        await sourcePipe.Writer.CompleteAsync();

        await compressor.DecompressAsync(sourcePipe.Reader, outputPipe.Writer);

        return await ReadAllFromPipeAsync(outputPipe.Reader);
    }

    private static async Task<byte[]> ReadAllFromPipeAsync(PipeReader reader)
    {
        using var ms = new MemoryStream();
        var result = await reader.ReadAsync();
        var buffer = result.Buffer;

        foreach (var segment in buffer)
        {
            await ms.WriteAsync(segment);
        }
        reader.AdvanceTo(buffer.End);
        await reader.CompleteAsync();

        return ms.ToArray();
    }
}
