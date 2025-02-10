namespace RayTree;

public interface INode
{
	string Id { get; }

	void Raise<TMessage>(TMessage message)
		where TMessage : class;
}