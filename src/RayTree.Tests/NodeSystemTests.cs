using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RayTree.Handlers;

namespace RayTree.Tests;

[TestFixture]
public class NodeSystemTests
{
	[Test]
	public void Test()
	{
		// Arrange
		var system = NodeSystem.Create();

		var handler = new TestHandler();
		system.Register(handler);

		// Act
		system.Raise(new TestMessage(value: 1));

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

	private sealed class TestHandler : IMessageHandler<TestMessage>
	{
		private readonly HashSet<int> _receivedValues = [];

		public Task HandleAsync(TestMessage message, CancellationToken cancellationToken)
		{
			ArgumentNullException.ThrowIfNull(message);

			_receivedValues.Add(message.Value);

			return Task.CompletedTask;
		}

		public bool CheckReceived(int value) => _receivedValues.Contains(value);
	}
}