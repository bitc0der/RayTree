using System.Threading;
using System.Threading.Tasks;

namespace RayTree.Handlers;

public interface IMessageHandler<TMessage>
	where TMessage : class
{
	Task HandleAsync(TMessage message, CancellationToken cancellationToken);
}