using RayTree.Location;
using System.Threading;
using System.Threading.Tasks;

namespace RayTree;

public interface INode
{
	NodeLocation Location { get; }

	ValueTask ProcessAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
		where TMessage : class;
}