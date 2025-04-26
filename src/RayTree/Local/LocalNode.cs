using System;
using System.Threading;
using System.Threading.Tasks;
using RayTree.Handlers;
using RayTree.Queues;

namespace RayTree.Local;

internal sealed class LocalNode : INode
{
	private readonly IQueue _inputQueue;
	private readonly IQueue _outputQueue;
	private readonly IMessageHandler _messageHandler;

	private readonly BackgroudJob _backgroudJob = new ();

	public string Id { get; }

	public LocalNode(string id, IQueue inputQueue, IMessageHandler messageHandler)
	{
		if (string.IsNullOrWhiteSpace(id))
			throw new ArgumentException($"'{nameof(id)}' cannot be null or whitespace.", nameof(id));

		Id = id;
		_inputQueue = inputQueue ?? throw new ArgumentNullException(nameof(inputQueue));
		_messageHandler = messageHandler ?? throw new ArgumentNullException(nameof(messageHandler));
	}

	public async ValueTask ProcessAsync<TMessage>(TMessage message, CancellationToken cancellationToken)
		where TMessage : class
	{
		ArgumentNullException.ThrowIfNull(message);

		await _inputQueue.SendAsync(message, cancellationToken);
	}

	public void Start()
	{
		_backgroudJob.Start(DoRoutineAsync);
	}

	public Task StopAsync() => _backgroudJob.StopAsync();

	private async Task DoRoutineAsync(CancellationToken cancellationToken)
	{
		var message = await _inputQueue.ReadMessageAsync(cancellationToken);

		var context = new NodeContext(outputQueue: _outputQueue);

		NodeContext.Set(context);
		try
		{
			await _messageHandler.HandleAsync(message, cancellationToken);
		}
		finally
		{
			NodeContext.Reset();
		}
	}
}