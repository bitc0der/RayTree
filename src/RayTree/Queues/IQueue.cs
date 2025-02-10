namespace RayTree.Queues;

public interface IQueue
{
	delegate void HandleMessage(object message);

	void Send<TMessage>(TMessage message)
		where TMessage : class;

	void Subscribe(HandleMessage handleMessage);

	void Unsubscribe(HandleMessage handleMessage);

	void Start();

	void Stop();
}