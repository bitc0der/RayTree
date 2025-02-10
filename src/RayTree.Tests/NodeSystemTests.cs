using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RayTree.Handlers;

namespace RayTree.Tests;

[TestFixture]
public sealed class NodeSystemTests
{
	[Test]
	public async Task Test()
	{
		// Arrange
		await using var system = NodeSystem.Create();

		var handler = new TestHandler();
		system.Register(handler);

		system.Start();

		// Act
		system.Raise(pipeName: "TestPipe", new TestMessage(value: 1));

		// Assert
		Assert.That(handler.CheckReceived(value: 1), Is.True);
	}

	private sealed class TestMessage
	{
		public int Value { get; }

		public TestMessage(int value)
		{
			Value = value;
		}
	}

	private sealed class TestHandler : IMessageHandler
	{
		private readonly HashSet<int> _receivedValues = [];

		public string Id => nameof(TestHandler);

		public Task HandleAsync(object message, CancellationToken cancellationToken)
		{
			ArgumentNullException.ThrowIfNull(message);

			if (message is TestMessage testMessage)
			{
				_receivedValues.Add(testMessage.Value);
			}

			return Task.CompletedTask;
		}

		public bool CheckReceived(int value) => _receivedValues.Contains(value);
	}
}