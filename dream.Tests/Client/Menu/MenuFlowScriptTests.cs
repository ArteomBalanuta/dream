using Dream.Client.Audio;
using Dream.Client.Menu;
using Xunit;

namespace Dream.Tests.Client.Menu;

public class MenuFlowScriptTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    public void IsEnabled_OnlyForANonZeroValue(string? value, bool expected)
    {
        Assert.Equal(expected, MenuFlowScript.IsEnabled(_ => value));
    }

    [Fact]
    public void AudioLog_IsOffUnlessRequested()
    {
        Assert.False(GameAudio.IsPlayLogRequested(_ => null));
        Assert.False(GameAudio.IsPlayLogRequested(_ => "0"));
        Assert.True(GameAudio.IsPlayLogRequested(_ => "1"));
    }

    [Fact]
    public void Script_WalksTheWholeFlowInOrder()
    {
        MenuFlowScript script = new();
        List<MenuFlowScript.Step> steps = new();
        bool inWorld = false;
        bool pause = false;
        bool root = true;
        for (int i = 0; i < 2000 && !script.Finished; i++)
        {
            MenuFlowScript.Command c = script.Tick(new MenuFlowScript.SceneState(inWorld, pause, root));
            switch (c.Step)
            {
                case MenuFlowScript.Step.StartSinglePlayer: inWorld = true; break;
                case MenuFlowScript.Step.OpenPauseWithEsc: pause = true; root = true; break;
                case MenuFlowScript.Step.OpenOptionsInPause: root = false; break;
                case MenuFlowScript.Step.EscBack: root = true; break;
                case MenuFlowScript.Step.ExitToMainMenu: inWorld = false; pause = false; break;
            }

            if (c.Step != MenuFlowScript.Step.None)
            {
                steps.Add(c.Step);
            }
        }

        Assert.Equal(
            new[]
            {
                MenuFlowScript.Step.StartSinglePlayer, MenuFlowScript.Step.OpenPauseWithEsc,
                MenuFlowScript.Step.OpenOptionsInPause, MenuFlowScript.Step.EscBack,
                MenuFlowScript.Step.ExitToMainMenu, MenuFlowScript.Step.StartSinglePlayer,
                MenuFlowScript.Step.OpenPauseWithEsc, MenuFlowScript.Step.ExitGame
            },
            steps);
    }

    [Fact]
    public void Script_FailsWhenTheWorldNeverArrives()
    {
        MenuFlowScript script = new();
        MenuFlowScript.Command last = default;
        for (int i = 0; i < 4000 && !script.Finished; i++)
        {
            MenuFlowScript.Command c = script.Tick(new MenuFlowScript.SceneState(false, false, true));
            if (c.Step != MenuFlowScript.Step.None)
            {
                last = c;
            }
        }

        Assert.Equal(MenuFlowScript.Step.Fail, last.Step);
    }

    [Fact]
    public void Script_FailsWhenEscDoesNotOpenThePause()
    {
        MenuFlowScript script = new();
        MenuFlowScript.Command last = default;
        bool inWorld = false;
        for (int i = 0; i < 2000 && !script.Finished; i++)
        {
            MenuFlowScript.Command c = script.Tick(new MenuFlowScript.SceneState(inWorld, false, true));
            if (c.Step == MenuFlowScript.Step.StartSinglePlayer)
            {
                inWorld = true;
            }

            if (c.Step != MenuFlowScript.Step.None)
            {
                last = c;
            }
        }

        Assert.Equal(MenuFlowScript.Step.Fail, last.Step);
    }
}
