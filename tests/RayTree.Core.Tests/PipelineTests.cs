using System.IO.Pipelines;
using RayTree.Models;
using RayTree.Plugins;
using RayTree.Plugins.Compressors.Gzip;
using RayTree.Plugins.Serializers.Json;
using RayTree.Tracking;

namespace RayTree.Core.Tests;

public class SerializationPipelineTests
{
    [Test]
    public async Task JsonSerializer_SerializeThenDeserialize_RoundTripsChange()
    {
        var serializer = new JsonSerializerPlugin();
        var change = new EntityChange
        {
            Id = 1,
            EntityType = "TestEntity",
            EntityId = "e-123",
            ChangeType = ChangeType.Update,
            Version = 3,
            CorrelationId = Guid.NewGuid(),
            Published = false
        };

        var pipe = new Pipe();

        await serializer.SerializeAsync(change, pipe.Writer);
        pipe.Writer.Complete();

        var result = await serializer.DeserializeAsync(pipe.Reader, "TestEntity");

        Assert.That(result.EntityType, Is.EqualTo(change.EntityType));
        Assert.That(result.EntityId, Is.EqualTo(change.EntityId));
        Assert.That(result.ChangeType, Is.EqualTo(change.ChangeType));
        Assert.That(result.Version, Is.EqualTo(change.Version));
        Assert.That(result.CorrelationId, Is.EqualTo(change.CorrelationId));
        Assert.That(result.Published, Is.EqualTo(change.Published));
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
        var change = new EntityChange
        {
            Id = 42,
            EntityType = "Customer",
            EntityId = "cust-001",
            ChangeType = ChangeType.Insert,
            Version = 1,
            CorrelationId = Guid.NewGuid()
        };

        var serializedBytes = await SerializeToBytesAsync(serializer, change);

        var compressOutput = await CompressDataAsync(compressor, serializedBytes);

        var decompressOutput = await DecompressDataAsync(compressor, compressOutput);

        var deserialized = await DeserializeFromBytesAsync(serializer, decompressOutput, change.EntityType);

        Assert.That(deserialized.EntityType, Is.EqualTo(change.EntityType));
        Assert.That(deserialized.EntityId, Is.EqualTo(change.EntityId));
        Assert.That(deserialized.ChangeType, Is.EqualTo(change.ChangeType));
        Assert.That(deserialized.Version, Is.EqualTo(change.Version));
    }

    private static async Task<byte[]> SerializeToBytesAsync(IChangeSerializer serializer, EntityChange change)
    {
        using var ms = new MemoryStream();
        var writer = PipeWriter.Create(ms);
        await serializer.SerializeAsync(change, writer);
        return ms.ToArray();
    }

    private static async Task<EntityChange> DeserializeFromBytesAsync(IChangeSerializer serializer, byte[] data, string entityType)
    {
        var reader = PipeReader.Create(new MemoryStream(data));
        return await serializer.DeserializeAsync(reader, entityType);
    }

    private static async Task<byte[]> CompressDataAsync(IChangeCompressor compressor, byte[] data)
    {
        var sourcePipe = new Pipe();
        var outputPipe = new Pipe();

        await sourcePipe.Writer.WriteAsync(data);
        sourcePipe.Writer.Complete();

        await compressor.CompressAsync(sourcePipe.Reader, outputPipe.Writer);

        return await ReadAllFromPipeAsync(outputPipe.Reader);
    }

    private static async Task<byte[]> DecompressDataAsync(IChangeCompressor compressor, byte[] data)
    {
        var sourcePipe = new Pipe();
        var outputPipe = new Pipe();

        await sourcePipe.Writer.WriteAsync(data);
        sourcePipe.Writer.Complete();

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
        reader.Complete();

        return ms.ToArray();
    }
}
