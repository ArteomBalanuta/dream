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
    public void Script_CapturesTheLoadingScreenAndPressesAKeyEachStart()
    {
        MenuFlowScript script = new();
        List<MenuFlowScript.Command> commands = new();
        bool inWorld = false;
        bool pause = false;
        bool root = true;
        int loadingFrames = 0; // frames since the start; the prompt shows from frame 60 until a key
        bool pressed = false;
        bool loading = false;
        for (int i = 0; i < 4000 && !script.Finished; i++)
        {
            bool awaiting = loading && loadingFrames >= 60 && !pressed;
            if (loading)
            {
                loadingFrames++;
            }

            MenuFlowScript.Command c = script.Tick(new MenuFlowScript.SceneState(inWorld, pause, root, loading && !pressed, awaiting));
            switch (c.Step)
            {
                case MenuFlowScript.Step.StartSinglePlayer: inWorld = true; loading = true; loadingFrames = 0; pressed = false; break;
                case MenuFlowScript.Step.PressAnyKey: pressed = true; loading = false; break;
                case MenuFlowScript.Step.OpenPauseWithEsc: pause = true; root = true; break;
                case MenuFlowScript.Step.OpenOptionsInPause: root = false; break;
                case MenuFlowScript.Step.EscBack: root = true; break;
                case MenuFlowScript.Step.ExitToMainMenu: inWorld = false; pause = false; break;
            }

            if (c.Step != MenuFlowScript.Step.None || c.CapturePng != null)
            {
                commands.Add(c);
            }
        }

        Assert.True(script.Finished);
        Assert.DoesNotContain(commands, c => c.Step == MenuFlowScript.Step.Fail);
        Assert.Equal(
            new[]
            {
                "flow-01-title.png", "flow-01a-loading.png", "flow-02a-loading-ready.png", "flow-02-world.png",
                "flow-03-pause.png", "flow-04-pause-options.png", "flow-05-pause-back.png", "flow-06-title-again.png",
                "flow-06a-loading.png", "flow-06b-loading-ready.png", "flow-07-world-again.png", "flow-08-pause-again.png"
            },
            commands.Select(c => c.CapturePng).ToArray());
        Assert.Equal(2, commands.Count(c => c.Step == MenuFlowScript.Step.PressAnyKey));
        Assert.All(commands.Where(c => c.CapturePng!.StartsWith("flow-01a") || c.CapturePng.StartsWith("flow-06a")),
            c => Assert.Equal(MenuFlowScript.Step.None, c.Step));
    }

    [Fact]
    public void Script_DoesNotEscapeWhileTheLoadingScreenIsUp()
    {
        MenuFlowScript script = new();
        bool inWorld = false;
        for (int i = 0; i < 1500; i++)
        {
            MenuFlowScript.Command c = script.Tick(new MenuFlowScript.SceneState(inWorld, false, true, LoadingVisible: inWorld, LoadingAwaitingKey: false));
            if (c.Step == MenuFlowScript.Step.StartSinglePlayer)
            {
                inWorld = true;
            }

            Assert.NotEqual(MenuFlowScript.Step.OpenPauseWithEsc, c.Step);
        }
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
