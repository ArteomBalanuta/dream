using Dream.Rendering.Core;
using Xunit;

namespace Dream.Tests.Rendering.Core;

public class BackgroundWorkTests
{
    [Fact]
    public void TryTakeCompleted_FromResult_ReturnsResultAndNullsTask()
    {
        Task<int>? task = Task.FromResult(42);

        bool taken = BackgroundWork.TryTakeCompleted(ref task, out int result, out Exception? fault);

        Assert.True(taken);
        Assert.Equal(42, result);
        Assert.Null(fault);
        Assert.Null(task);
    }

    [Fact]
    public void TryTakeCompleted_FromException_ReturnsFaultWithoutThrowingAndNullsTask()
    {
        InvalidOperationException error = new("build failed");
        Task<string>? task = Task.FromException<string>(error);

        bool taken = BackgroundWork.TryTakeCompleted(ref task, out string? result, out Exception? fault);

        Assert.False(taken);
        Assert.Null(result);
        Assert.Same(error, fault);
        Assert.Null(task);
    }

    [Fact]
    public void TryTakeCompleted_UnwrapsNestedAggregateToFirstInnerException()
    {
        InvalidOperationException inner = new("inner");
        Task<int>? task = Task.FromException<int>(new AggregateException(new AggregateException(inner)));

        BackgroundWork.TryTakeCompleted(ref task, out int _, out Exception? fault);

        Assert.Same(inner, fault);
    }

    [Fact]
    public void TryTakeCompleted_CanceledTask_ReportsFault()
    {
        Task<int>? task = Task.FromCanceled<int>(new CancellationToken(canceled: true));

        bool taken = BackgroundWork.TryTakeCompleted(ref task, out int _, out Exception? fault);

        Assert.False(taken);
        Assert.IsAssignableFrom<OperationCanceledException>(fault);
        Assert.Null(task);
    }

    [Fact]
    public void TryTakeCompleted_PendingTask_ReturnsFalseAndLeavesTaskUntouched()
    {
        TaskCompletionSource<int> source = new();
        Task<int> pending = source.Task;
        Task<int>? task = pending;

        bool taken = BackgroundWork.TryTakeCompleted(ref task, out int _, out Exception? fault);

        Assert.False(taken);
        Assert.Null(fault);
        Assert.Same(pending, task);
    }

    [Fact]
    public void TryTakeCompleted_NullTask_ReturnsFalse()
    {
        Task<int>? task = null;

        bool taken = BackgroundWork.TryTakeCompleted(ref task, out int _, out Exception? fault);

        Assert.False(taken);
        Assert.Null(fault);
    }

    [Fact]
    public void ObserveAndForget_ReportsLaterFault()
    {
        RenderFaultLog log = new(_ => { });
        TaskCompletionSource<int> source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        InvalidOperationException error = new("late");

        BackgroundWork.ObserveAndForget(source.Task, log, "test-source");
        Assert.Equal(0, log.FaultCount);
        source.SetException(error);

        Assert.True(SpinWait.SpinUntil(() => log.FaultCount == 1, TimeSpan.FromSeconds(5)));
        Assert.Same(error, log.LastFault);
        Assert.Equal(1, log.GetFaultCount("test-source"));
    }

    [Fact]
    public void ObserveAndForget_DoesNotReportSuccess()
    {
        RenderFaultLog log = new(_ => { });
        Task<int> task = Task.FromResult(1);

        BackgroundWork.ObserveAndForget(task, log, "test-source");
        BackgroundWork.ObserveAndForget(null, log, "test-source");

        Assert.Equal(0, log.FaultCount);
    }
}
