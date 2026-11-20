using Dream.Client.Startup;
using Xunit;

namespace Dream.Tests.Client.Startup;

public class EnterAltLatchTests
{
    [Fact]
    public void AltReleasedBeforeEnter_NeverPresses()
    {
        EnterAltLatch latch = new();
        Assert.False(latch.Update(false, true));
        Assert.False(latch.Update(true, true));
        Assert.False(latch.Update(true, false));
        Assert.False(latch.Update(false, false));
    }

    [Fact]
    public void PlainEnter_Presses_AndALaterAltEnterStillSuppressed()
    {
        EnterAltLatch latch = new();
        Assert.True(latch.Update(true, false));
        Assert.False(latch.Update(false, false));
        Assert.False(latch.Update(true, true));
        Assert.False(latch.Update(false, false));
        Assert.True(latch.Update(true, false));
    }
}
