using Xunit;
using Dream.Application;

namespace Dream.Tests.Application;

public class ChangedTextSinkTests
{
    [Fact]
    public void Set_AppliesOnlyWhenTextChanges()
    {
        // Game set the console and window titles every frame through synchronous Win32 calls.
        List<string> applied = [];
        ChangedTextSink sink = new(applied.Add);

        sink.Set("a");
        sink.Set("a");
        sink.Set("b");
        sink.Set("b");

        Assert.Equal(["a", "b"], applied);
        Assert.Equal(2, sink.ApplyCount);
    }
}
