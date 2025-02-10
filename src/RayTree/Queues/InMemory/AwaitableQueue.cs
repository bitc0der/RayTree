using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

internal sealed class AwaitableQueue<T> : IDisposable
{
	private readonly ConcurrentQueue<T> _eventQueue = new();
	private readonly AutoResetEvent _autoResetEvent = new(initialState: false);

	private bool _isDisposed = false;

	public void Add(T item)
	{
		CheckDisposed();

		_eventQueue.Enqueue(item);
		_autoResetEvent.Set();
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void CheckDisposed() => ObjectDisposedException.ThrowIf(_isDisposed, instance: this);

	public void Dispose()
	{
		if (_isDisposed) return;

		_autoResetEvent.Dispose();

		_isDisposed = true;
	}

	public async Task<T> GetAsync(CancellationToken cancellationToken = default)
	{
		CheckDisposed();

		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (_eventQueue.TryDequeue(out var @event))
				return @event;

			await _autoResetEvent.WaitAsync(cancellationToken: cancellationToken);
		}
	}
}