using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

internal sealed class BackgroudJob : IAsyncDisposable
{
	private CancellationTokenSource? _cancellationTokenSource;
	private Task? _task;

	private int _state = (int)JobState.Stopped;

	private bool _disposed = false;

	public void Start(Func<CancellationToken, Task> func)
	{
		ArgumentNullException.ThrowIfNull(func);

		CheckDisposed();

		if (TrySetState(target: JobState.Starting, check: JobState.Stopped))
		{
			_cancellationTokenSource = new();

			_task = DoRoutineAsync(func, cancellationToken: _cancellationTokenSource.Token)
				.ContinueWith(
					continuationAction: t => TrySetState(target: JobState.Stopped, check: JobState.Stopping),
					continuationOptions: TaskContinuationOptions.ExecuteSynchronously
				);

			TrySetState(target: JobState.Started, check: JobState.Starting);
		}
	}

	private async Task DoRoutineAsync(Func<CancellationToken, Task> func, CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				await func(cancellationToken);
			}
			catch (OperationCanceledException)
			{
				break;
			}
		}
	}

	public async Task StopAsync()
	{
		CheckDisposed();

		if (TrySetState(target: JobState.Stopping, check: JobState.Started))
		{
			Debug.Assert(_cancellationTokenSource is not null);

			_cancellationTokenSource.Cancel();

			Debug.Assert(_task is not null);

			await _task;
		}
	}

	private bool TrySetState(JobState target, JobState check)
	{
		var original = (int)check;

		var result = Interlocked.CompareExchange(ref _state, (int)target, original);

		return result == original;
	}

	private enum JobState
	{
		Stopped = 0,
		Starting,
		Started,
		Stopping
	}

	private void CheckDisposed() => ObjectDisposedException.ThrowIf(_disposed, instance: this);

	public async ValueTask DisposeAsync()
	{
		if (_disposed)
			return;

		await StopAsync().ConfigureAwait(false);

		_disposed = true;
	}
}