namespace Uplift.Jobs;

/// <summary>
///     Threads with deep stacks: the compiler's parsers recurse once per nesting level of a script, and some scripts nest deeply. Each
///     worker thread reserves <see cref="StackSize" /> of address space (only what is used is ever committed).
/// </summary>
public static class Workers
{
	public const int StackSize = 256 * 1024 * 1024;

	/// <summary>A sensible number of workers: half the processors, at least one, at most eight.</summary>
	public static int DefaultParallelism => Math.Clamp(Environment.ProcessorCount / 2, 1, 8);

	/// <summary>Runs a function on its own deep-stack thread.</summary>
	public static Task<T> Run<T>(Func<T> work)
	{
		var source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
		var thread = new Thread(() =>
		{
			try
			{
				source.SetResult(work());
			}
			catch (Exception e)
			{
				source.SetException(e);
			}
		}, StackSize) { IsBackground = true, Name = "uplift-worker" };
		thread.Start();
		return source.Task;
	}

	/// <summary>Runs the body for every item on <paramref name="parallelism" /> deep-stack threads; returns when all are done.</summary>
	public static void ForEach<T>(IReadOnlyList<T> items, int parallelism, Action<T> body, CancellationToken cancellation = default)
	{
		var next = -1;
		Exception? failure = null;
		var threads = new List<Thread>();
		for (var w = 0; w < Math.Max(1, Math.Min(parallelism, items.Count)); w++)
		{
			var thread = new Thread(() =>
			{
				while (failure == null && !cancellation.IsCancellationRequested)
				{
					var i = Interlocked.Increment(ref next);
					if (i >= items.Count) return;
					try
					{
						body(items[i]);
					}
					catch (Exception e)
					{
						Interlocked.CompareExchange(ref failure, e, null);
					}
				}
			}, StackSize) { IsBackground = true, Name = "uplift-worker-" + w };
			threads.Add(thread);
			thread.Start();
		}
		foreach (var t in threads) t.Join();
		if (failure != null) throw new AggregateException(failure);
		cancellation.ThrowIfCancellationRequested();
	}
}
