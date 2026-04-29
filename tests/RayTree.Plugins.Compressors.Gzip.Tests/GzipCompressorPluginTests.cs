using System.IO.Compression;
using System.IO.Pipelines;
using System.Text;

namespace RayTree.Plugins.Compressors.Gzip.Tests;

public class GzipCompressorPluginTests
{
    [Test]
    public async Task CompressThenDecompress_RoundTrip_PreservesData()
    {
        var compressor = new GzipCompressorPlugin();
        var originalData = Encoding.UTF8.GetBytes("Hello, World! This is test data for compression.");

        var compressed = await CompressAndCaptureAsync(compressor, originalData);
        var result = await DecompressAndCaptureAsync(compressor, compressed);

        Assert.That(result, Is.EqualTo(originalData));
    }

    [Test]
    public async Task CompressAsync_WithOptimalLevel_CompressesData()
    {
        var compressor = new GzipCompressorPlugin(CompressionLevel.Optimal);
        var originalData = Encoding.UTF8.GetBytes(new string('A', 10000));

        var compressed = await CompressAndCaptureAsync(compressor, originalData);
        Assert.That(compressed.Length, Is.LessThan(originalData.Length));
    }

    [Test]
    public async Task CompressAsync_WithFastestLevel_CompressesFaster()
    {
        var compressor = new GzipCompressorPlugin(CompressionLevel.Fastest);
        var originalData = Encoding.UTF8.GetBytes(new string('B', 10000));

        var compressed = await CompressAndCaptureAsync(compressor, originalData);
        Assert.That(compressed.Length, Is.LessThan(originalData.Length));
    }

    [Test]
    public async Task CompressAsync_WithNoCompression_PassThrough()
    {
        var compressor = new GzipCompressorPlugin(CompressionLevel.NoCompression);
        var originalData = Encoding.UTF8.GetBytes("Test data");

        var compressed = await CompressAndCaptureAsync(compressor, originalData);
        Assert.That(compressed.Length, Is.GreaterThanOrEqualTo(originalData.Length));
    }

    [Test]
    public void Name_ReturnsGzip()
    {
        var compressor = new GzipCompressorPlugin();
        Assert.That(compressor.Name, Is.EqualTo("Gzip"));
    }

    [Test]
    public async Task CompressThenDecompress_EmptyData_Succeeds()
    {
        var compressor = new GzipCompressorPlugin();
        var originalData = Array.Empty<byte>();

        var compressed = await CompressAndCaptureAsync(compressor, originalData);
        var result = await DecompressAndCaptureAsync(compressor, compressed);

        Assert.That(result, Is.Empty);
    }

    [Test]
    public async Task CompressThenDecompress_LargeData_PreservesData()
    {
        var compressor = new GzipCompressorPlugin();
        var originalData = Encoding.UTF8.GetBytes(new string('X', 100000));

        var compressed = await CompressAndCaptureAsync(compressor, originalData);
        var result = await DecompressAndCaptureAsync(compressor, compressed);

        Assert.That(result, Is.EqualTo(originalData));
    }

    private static async Task<byte[]> CompressAndCaptureAsync(GzipCompressorPlugin compressor, byte[] data)
    {
        var ms = new MemoryStream();
        var pipeWriter = PipeWriter.Create(ms);
        var readPipe = PipeReader.Create(new MemoryStream(data));
        await compressor.CompressAsync(readPipe, pipeWriter);
        return ms.ToArray();
    }

    private static async Task<byte[]> DecompressAndCaptureAsync(GzipCompressorPlugin compressor, byte[] data)
    {
        var ms = new MemoryStream();
        var pipeWriter = PipeWriter.Create(ms);
        var readPipe = PipeReader.Create(new MemoryStream(data));
        await compressor.DecompressAsync(readPipe, pipeWriter);
        return ms.ToArray();
    }
}
