using System.Threading;
using System.Threading.Tasks;

namespace RayTree;

public interface INode
{
	string Id { get; }

	ValueTask ProcessAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
		where TMessage : class;
}