using Dream.Client.Menu;
using Dream.Client.Startup;
using Xunit;

namespace Dream.Tests.Client.Startup;

public class CaptureAutoConnectTests
{
    private static Func<string, string?> Env(string? autoConnect = null, string? capture = null) => key => key switch
    {
        "DREAM_AUTO_CONNECT" => autoConnect,
        MenuLaunchPolicy.HudCaptureEnvironmentVariable => capture,
        _ => null,
    };

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("YES")]
    public void AutoConnectVariable_Enables(string value)
    {
        Assert.True(StartupConnectionScene.IsAutoConnectEnabled(Env(autoConnect: value)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("no")]
    [InlineData("")]
    public void AutoConnectVariable_OtherValues_DoNotEnable(string value)
    {
        Assert.False(StartupConnectionScene.IsAutoConnectEnabled(Env(autoConnect: value)));
    }

    [Fact]
    public void NothingSet_IsOff()
    {
        Assert.False(StartupConnectionScene.IsAutoConnectEnabled(Env()));
    }

    [Fact]
    public void CaptureScript_ImpliesAutoConnect()
    {
        Assert.True(StartupConnectionScene.IsAutoConnectEnabled(Env(capture: "play,snap")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankCaptureVariable_DoesNotImplyAutoConnect(string capture)
    {
        Assert.False(StartupConnectionScene.IsAutoConnectEnabled(Env(capture: capture)));
    }
}
