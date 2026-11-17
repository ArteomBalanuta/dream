namespace Dream.Client.Menu;

// DREAM_MENU_FLOW=1 (with DREAM_MENU_CAPTURE=<dir> for the PNG folder and the unfocused start): drives
// Single Player -> world -> Esc pause menu -> Options -> Esc back -> Exit to Main Menu -> Single Player
// again -> Exit Game, one step per rendered frame, logging each step. Verification only; with the
// variable unset nothing about it runs. Pure state machine: Game feeds it the scene state and runs
// the command it returns.
public sealed class MenuFlowScript
{
    public const string EnvironmentVariable = "DREAM_MENU_FLOW";

    public enum Step
    {
        None,
        StartSinglePlayer,
        OpenPauseWithEsc,
        OpenOptionsInPause,
        EscBack,
        ExitToMainMenu,
        ExitGame,
        Fail,
        PressAnyKey
    }

    public readonly record struct Command(Step Step, string? CapturePng = null, string? Log = null);

    public readonly record struct SceneState(
        bool InWorld,
        bool PauseVisible,
        bool PauseOnRoot,
        bool LoadingVisible = false,
        bool LoadingAwaitingKey = false);

    private const int SettleFrames = 30;
    private const int WorldFrames = 150;
    private const int LoadingPanelFrames = 5;
    private const int TimeoutFrames = 3000;

    private int _phase;
    private int _wait;
    private int _timeout;
    private int _hold;

    public static bool IsEnabled(Func<string, string?> environment)
    {
        string? value = environment(EnvironmentVariable);
        return !string.IsNullOrWhiteSpace(value) && value.Trim() != "0";
    }

    public bool Finished { get; private set; }

    public Command Tick(SceneState s)
    {
        if (Finished)
        {
            return default;
        }

        if (++_timeout > TimeoutFrames)
        {
            Finished = true;
            return new Command(Step.Fail, null, $"timed out in phase {_phase}");
        }

        bool settled = ++_wait > SettleFrames;
        switch (_phase)
        {
            case 0: // first title screen
                if (!s.InWorld && settled)
                {
                    return Go(1, new Command(Step.StartSinglePlayer, "flow-01-title.png", "title -> Single Player"));
                }

                break;
            case 1: // the loading panel, a few frames after the start
                if (_wait > LoadingPanelFrames)
                {
                    return Go(2, new Command(Step.None, "flow-01a-loading.png", "loading screen shown"));
                }

                break;
            case 2: // loading until the prompt has shown for a while, then press a key
                if (AwaitedPrompt(s, "flow-02a-loading-ready.png", out Command press))
                {
                    return Go(3, press);
                }

                if (WorldShown(s))
                {
                    _phase = 3; // no loading screen at all: straight to the world
                }

                break;
            case 3: // the world, with the gate open
                if (WorldShown(s))
                {
                    return Go(4, new Command(Step.OpenPauseWithEsc, "flow-02-world.png", "world reached; Esc"));
                }

                break;
            case 4:
                if (settled)
                {
                    return s.PauseVisible && s.PauseOnRoot
                        ? Go(5, new Command(Step.OpenOptionsInPause, "flow-03-pause.png", "pause menu open at root; Options"))
                        : Fail("Esc did not open the pause menu at its root");
                }

                break;
            case 5:
                if (settled)
                {
                    return s.PauseVisible && !s.PauseOnRoot
                        ? Go(6, new Command(Step.EscBack, "flow-04-pause-options.png", "pause Options shown; Esc"))
                        : Fail("Options did not open inside the pause menu");
                }

                break;
            case 6:
                if (settled)
                {
                    return s.PauseVisible && s.PauseOnRoot
                        ? Go(7, new Command(Step.ExitToMainMenu, "flow-05-pause-back.png", "Esc stepped back to the pause root; Exit to Main Menu"))
                        : Fail("Esc did not step back to the pause root (or closed the menu)");
                }

                break;
            case 7: // waiting for the title screen
                if (!s.InWorld && settled)
                {
                    return Go(8, new Command(Step.StartSinglePlayer, "flow-06-title-again.png", "back at the title; Single Player (second start)"));
                }

                break;
            case 8:
                if (_wait > LoadingPanelFrames)
                {
                    return Go(9, new Command(Step.None, "flow-06a-loading.png", "loading screen shown (second start)"));
                }

                break;
            case 9:
                if (AwaitedPrompt(s, "flow-06b-loading-ready.png", out Command pressAgain))
                {
                    return Go(10, pressAgain);
                }

                if (WorldShown(s))
                {
                    _phase = 10;
                }

                break;
            case 10:
                if (WorldShown(s))
                {
                    return Go(11, new Command(Step.OpenPauseWithEsc, "flow-07-world-again.png", "second world reached; Esc"));
                }

                break;
            case 11:
                if (settled)
                {
                    Finished = true;
                    return s.PauseVisible
                        ? new Command(Step.ExitGame, "flow-08-pause-again.png", "Exit Game")
                        : new Command(Step.Fail, null, "Esc did not open the pause menu the second time");
                }

                break;
        }

        return default;
    }

    // The prompt has held for SettleFrames in a row.
    private bool AwaitedPrompt(SceneState s, string png, out Command press)
    {
        _hold = s.LoadingAwaitingKey ? _hold + 1 : 0;
        press = default;
        if (_hold <= SettleFrames)
        {
            return false;
        }

        press = new Command(Step.PressAnyKey, png, "loading ready; any key");
        return true;
    }

    private bool WorldShown(SceneState s) => s.InWorld && !s.LoadingVisible && _wait > WorldFrames;

    private Command Go(int phase, Command command)
    {
        _phase = phase;
        _wait = 0;
        _hold = 0;
        return command;
    }

    private Command Fail(string message)
    {
        Finished = true;
        return new Command(Step.Fail, null, message);
    }
}
