using RayTree.Queues;
using System;

namespace RayTree.Handlers;

internal sealed class MessageHandlerWrapper
{
	private readonly IQueue _inputQueue;
	private readonly IMessageHandler _handler;

	public MessageHandlerWrapper(IQueue inputQueue, IMessageHandler handler)
	{
		_inputQueue = inputQueue ?? throw new ArgumentNullException(nameof(inputQueue));
		_handler = handler ?? throw new ArgumentNullException(nameof(handler));
	}
}
