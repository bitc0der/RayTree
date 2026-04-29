using System.IO.Pipelines;
using System.Text;

namespace RayTree.Plugins.Compressors.Lz4.Tests;

public class Lz4CompressorPluginTests
{
    [Test]
    public async Task CompressThenDecompress_RoundTrip_PreservesData()
    {
        var compressor = new Lz4CompressorPlugin();
        var originalData = Encoding.UTF8.GetBytes("Hello, World! This is test data for LZ4 compression.");

        var compressed = await CompressAndCaptureAsync(compressor, originalData);
        var result = await DecompressAndCaptureAsync(compressor, compressed);

        Assert.That(result, Is.EqualTo(originalData));
    }

    [Test]
    public async Task CompressAsync_CompressesRepetitiveData()
    {
        var compressor = new Lz4CompressorPlugin();
        var originalData = Encoding.UTF8.GetBytes(new string('A', 1000));

        var compressed = await CompressAndCaptureAsync(compressor, originalData);
        Assert.That(compressed.Length, Is.LessThan(originalData.Length));
    }

    [Test]
    public void Name_ReturnsLZ4()
    {
        var compressor = new Lz4CompressorPlugin();
        Assert.That(compressor.Name, Is.EqualTo("LZ4"));
    }

    [Test]
    public async Task CompressThenDecompress_CompressibleData_PreservesData()
    {
        var compressor = new Lz4CompressorPlugin();
        var originalData = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Hello World! ", 50)));

        var compressed = await CompressAndCaptureAsync(compressor, originalData);
        var result = await DecompressAndCaptureAsync(compressor, compressed);

        Assert.That(result, Is.EqualTo(originalData));
    }

    private static async Task<byte[]> CompressAndCaptureAsync(Lz4CompressorPlugin compressor, byte[] data)
    {
        var ms = new MemoryStream();
        var pipeWriter = PipeWriter.Create(ms);
        var readPipe = PipeReader.Create(new MemoryStream(data));
        await compressor.CompressAsync(readPipe, pipeWriter);
        return ms.ToArray();
    }

    private static async Task<byte[]> DecompressAndCaptureAsync(Lz4CompressorPlugin compressor, byte[] data)
    {
        var ms = new MemoryStream();
        var pipeWriter = PipeWriter.Create(ms);
        var readPipe = PipeReader.Create(new MemoryStream(data));
        await compressor.DecompressAsync(readPipe, pipeWriter);
        return ms.ToArray();
    }
}
