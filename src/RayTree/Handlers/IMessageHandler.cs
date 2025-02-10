using System.Threading;
using System.Threading.Tasks;

namespace RayTree.Handlers;

public interface IMessageHandler
{
	string Id { get; }

	Task HandleAsync(object message, CancellationToken cancellationToken);
}