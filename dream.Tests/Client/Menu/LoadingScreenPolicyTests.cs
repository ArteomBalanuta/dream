using Dream.Client.Menu;
using Dream.Client.Scenes;
using Xunit;

namespace Dream.Tests.Client.Menu;

public class LoadingScreenPolicyTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] pairs) =>
        name => pairs.Where(p => p.Key == name).Select(p => (string?)p.Value).FirstOrDefault();

    [Fact]
    public void FromTheMainMenu_WaitsForAKey()
    {
        Assert.Equal(LoadingGateMode.WaitForKey, LoadingScreenPolicy.Resolve(true, Env()));
    }

    [Fact]
    public void NotFromTheMainMenu_IsOff()
    {
        Assert.Equal(LoadingGateMode.Off, LoadingScreenPolicy.Resolve(false, Env()));
    }

    [Theory]
    [InlineData("out")]
    [InlineData("C:\\caps")]
    public void HudCapture_IsOff(string value)
    {
        Assert.Equal(LoadingGateMode.Off, LoadingScreenPolicy.Resolve(true, Env(("DREAM_HUD_CAPTURE", value))));
    }

    [Fact]
    public void BlankHudCapture_DoesNotTurnItOff()
    {
        Assert.Equal(LoadingGateMode.WaitForKey, LoadingScreenPolicy.Resolve(true, Env(("DREAM_HUD_CAPTURE", "  "))));
    }

    [Fact]
    public void PromptZero_IsOff_OtherValuesAreNot()
    {
        Assert.Equal(LoadingGateMode.Off, LoadingScreenPolicy.Resolve(true, Env((LoadingScreenPolicy.PromptEnvironmentVariable, "0"))));
        Assert.Equal(LoadingGateMode.WaitForKey, LoadingScreenPolicy.Resolve(true, Env((LoadingScreenPolicy.PromptEnvironmentVariable, "1"))));
    }

    [Fact]
    public void MenuFlow_StillWaitsForAKey()
    {
        Assert.Equal(LoadingGateMode.WaitForKey, LoadingScreenPolicy.Resolve(true, Env(("DREAM_MENU_FLOW", "1"))));
    }
}
