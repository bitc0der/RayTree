using System.IO.Compression;
using System.Text;

namespace RayTree.Plugins.Compressors.Brotli.Tests;

public class BrotliCompressorPluginTests
{
    [Test]
    public async Task CompressThenDecompress_RoundTrip_PreservesData()
    {
        var compressor = new BrotliCompressorPlugin();
        var originalData = Encoding.UTF8.GetBytes("Hello, World! This is test data for Brotli compression.");

        var compressed = await CompressAndCaptureAsync(compressor, originalData);
        var result = await DecompressAndCaptureAsync(compressor, compressed);

        Assert.That(result, Is.EqualTo(originalData));
    }

    [Test]
    public async Task CompressAsync_WithOptimalLevel_CompressesData()
    {
        var compressor = new BrotliCompressorPlugin(CompressionLevel.Optimal);
        var originalData = Encoding.UTF8.GetBytes(new string('A', 10000));

        var compressed = await CompressAndCaptureAsync(compressor, originalData);
        Assert.That(compressed.Length, Is.LessThan(originalData.Length));
    }

    [Test]
    public async Task CompressAsync_WithFastestLevel_CompressesFaster()
    {
        var compressor = new BrotliCompressorPlugin(CompressionLevel.Fastest);
        var originalData = Encoding.UTF8.GetBytes(new string('B', 10000));

        var compressed = await CompressAndCaptureAsync(compressor, originalData);
        Assert.That(compressed.Length, Is.LessThan(originalData.Length));
    }

    [Test]
    public async Task CompressAsync_WithNoCompression_PassThrough()
    {
        var compressor = new BrotliCompressorPlugin(CompressionLevel.NoCompression);
        var originalData = Encoding.UTF8.GetBytes("Test data");

        var compressed = await CompressAndCaptureAsync(compressor, originalData);
        Assert.That(compressed.Length, Is.GreaterThanOrEqualTo(originalData.Length));
    }

    [Test]
    public void Name_ReturnsBrotli()
    {
        var compressor = new BrotliCompressorPlugin();
        Assert.That(compressor.Name, Is.EqualTo("Brotli"));
    }

    [Test]
    public async Task CompressThenDecompress_LargeData_PreservesData()
    {
        var compressor = new BrotliCompressorPlugin();
        var originalData = Encoding.UTF8.GetBytes(new string('Y', 100000));

        var compressed = await CompressAndCaptureAsync(compressor, originalData);
        var result = await DecompressAndCaptureAsync(compressor, compressed);

        Assert.That(result, Is.EqualTo(originalData));
    }

    [Test]
    public async Task CompressAsync_RepetitiveData_HighCompressionRatio()
    {
        var compressor = new BrotliCompressorPlugin(CompressionLevel.Optimal);
        var originalData = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("ABCDEFGHIJKLMNOPQRSTUVWXYZ", 1000)));

        var compressed = await CompressAndCaptureAsync(compressor, originalData);
        var ratio = (double)compressed.Length / originalData.Length;
        Assert.That(ratio, Is.LessThan(0.1));
    }

    private static async Task<byte[]> CompressAndCaptureAsync(BrotliCompressorPlugin compressor, byte[] data)
    {
        using var output = new MemoryStream();
        await compressor.CompressAsync(new MemoryStream(data), output);
        return output.ToArray();
    }

    private static async Task<byte[]> DecompressAndCaptureAsync(BrotliCompressorPlugin compressor, byte[] data)
    {
        using var output = new MemoryStream();
        await compressor.DecompressAsync(new MemoryStream(data), output);
        return output.ToArray();
    }
}
