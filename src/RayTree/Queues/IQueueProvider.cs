namespace RayTree.Queues;

public interface IQueueProvider
{
	IQueue Create(string queueName);
}