using System.Threading;
using System.Threading.Tasks;

namespace RayTree.Queues;

public interface IQueue
{
	string Name { get; }

	Task SendAsync<TMessage>(TMessage message, CancellationToken cancellationToken)
		where TMessage : class;

	Task<object> ReadMessageAsync(CancellationToken cancellationToken);

	public void Start();

	public Task StopAsync();
}