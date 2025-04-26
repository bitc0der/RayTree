using System.Threading;
using System.Threading.Tasks;

namespace RayTree.Handlers;

public interface IMessageHandler
{
	ValueTask HandleAsync(object message, CancellationToken cancellationToken);
}