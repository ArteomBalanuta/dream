namespace Dream.Rendering.Core;

public static class BackgroundWork
{
    // Takes a finished task's outcome without ever rethrowing on the caller's thread.
    public static bool TryTakeCompleted<T>(ref Task<T>? task, out T? result, out Exception? fault)
    {
        result = default;
        fault = null;
        Task<T>? current = task;
        if (current == null || !current.IsCompleted)
        {
            return false;
        }

        task = null;
        if (current.IsCompletedSuccessfully)
        {
            result = current.Result;
            return true;
        }

        fault = UnwrapFault(current);
        return false;
    }

    public static void ObserveAndForget(Task? task, IRenderFaultSink sink, string source)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (task == null)
        {
            return;
        }

        task.ContinueWith(
            static (completed, state) =>
            {
                (IRenderFaultSink faultSink, string faultSource) = ((IRenderFaultSink, string))state!;
                faultSink.Report(faultSource, UnwrapFault(completed));
            },
            (sink, source),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static Exception UnwrapFault(Task task)
    {
        if (task.IsCanceled)
        {
            return new TaskCanceledException(task);
        }

        AggregateException? aggregate = task.Exception;
        if (aggregate == null)
        {
            return new InvalidOperationException("Background work did not complete successfully.");
        }

        AggregateException flattened = aggregate.Flatten();
        return flattened.InnerExceptions.Count > 0
            ? flattened.InnerExceptions[0]
            : flattened;
    }
}
