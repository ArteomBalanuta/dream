using Xunit;

namespace Dream.Tests;

public class TestAssemblyInitializerTests
{
    [Fact]
    public void ThreadPool_Minimum_Is_Raised_Before_Any_Test_Runs()
    {
        int target = Environment.ProcessorCount * TestAssemblyInitializer.ThreadsPerCore;

        ThreadPool.GetMinThreads(out int worker, out int io);

        Assert.True(worker >= target, $"Worker minimum is {worker}; expected at least {target}.");
        Assert.True(io >= target, $"IO minimum is {io}; expected at least {target}.");
    }
}
