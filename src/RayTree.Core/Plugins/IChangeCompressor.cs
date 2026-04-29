using System.IO.Pipelines;

namespace RayTree.Plugins;

public interface IChangeCompressor
{
    string Name { get; }
    Task CompressAsync(PipeReader source, PipeWriter destination, CancellationToken cancellationToken = default);
    Task DecompressAsync(PipeReader source, PipeWriter destination, CancellationToken cancellationToken = default);
}
