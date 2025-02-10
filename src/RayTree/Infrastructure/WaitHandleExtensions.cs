using System;
using System.Threading;
using System.Threading.Tasks;

internal static class WaitHandleExtensions
{
	public static Task WaitAsync(this WaitHandle handle, TimeSpan timeout, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(handle);

		return WaitAsync(
			handle: handle,
			timeoutMs: Convert.ToInt32(timeout.TotalMilliseconds),
			cancellationToken: cancellationToken
		);
	}

	public static Task WaitAsync(this WaitHandle handle, int timeoutMs = Timeout.Infinite, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(handle);

		var tcs = new TaskCompletionSource();
		var ctr = cancellationToken.CanBeCanceled
			? cancellationToken.Register(tcs.SetCanceled)
			: default;

		var rwh = ThreadPool.RegisterWaitForSingleObject(
			waitObject: handle,
			callBack: (stete, timeout) =>
			{
				if (stete is TaskCompletionSource completionSource)
				{
					if (completionSource.Task.IsCompleted)
						return;

					if (timeout)
						completionSource.SetCanceled();
					else
						completionSource.SetResult();
				}
				else
					throw new InvalidOperationException();
			},
			state: tcs,
			millisecondsTimeOutInterval: timeoutMs,
			executeOnlyOnce: true
		);

		tcs.Task.ContinueWith(
			continuationFunction: result =>
			{
				rwh.Unregister(null);
				return !cancellationToken.CanBeCanceled || ctr.Unregister();
			},
			continuationOptions: TaskContinuationOptions.ExecuteSynchronously
		);

		return tcs.Task;
	}
}