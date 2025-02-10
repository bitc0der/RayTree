namespace RayTree.Queues;

public interface IQueue
{
	delegate void HandleMessage(object message);

	string Name { get; }

	void Send<TMessage>(TMessage message)
		where TMessage : class;

	void Subscribe(HandleMessage handleMessage);

	void Unsubscribe(HandleMessage handleMessage);
}