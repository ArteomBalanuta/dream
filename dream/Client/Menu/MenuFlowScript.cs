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
    private const int TimeoutFrames = 3000;

    private int _phase;
    private int _wait;
    private int _timeout;

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
            case 1: // waiting for the world
                if (s.InWorld && _wait > WorldFrames)
                {
                    return Go(2, new Command(Step.OpenPauseWithEsc, "flow-02-world.png", "world reached; Esc"));
                }

                break;
            case 2:
                if (settled)
                {
                    return s.PauseVisible && s.PauseOnRoot
                        ? Go(3, new Command(Step.OpenOptionsInPause, "flow-03-pause.png", "pause menu open at root; Options"))
                        : Fail("Esc did not open the pause menu at its root");
                }

                break;
            case 3:
                if (settled)
                {
                    return s.PauseVisible && !s.PauseOnRoot
                        ? Go(4, new Command(Step.EscBack, "flow-04-pause-options.png", "pause Options shown; Esc"))
                        : Fail("Options did not open inside the pause menu");
                }

                break;
            case 4:
                if (settled)
                {
                    return s.PauseVisible && s.PauseOnRoot
                        ? Go(5, new Command(Step.ExitToMainMenu, "flow-05-pause-back.png", "Esc stepped back to the pause root; Exit to Main Menu"))
                        : Fail("Esc did not step back to the pause root (or closed the menu)");
                }

                break;
            case 5: // waiting for the title screen
                if (!s.InWorld && settled)
                {
                    return Go(6, new Command(Step.StartSinglePlayer, "flow-06-title-again.png", "back at the title; Single Player (second start)"));
                }

                break;
            case 6:
                if (s.InWorld && _wait > WorldFrames)
                {
                    return Go(7, new Command(Step.OpenPauseWithEsc, "flow-07-world-again.png", "second world reached; Esc"));
                }

                break;
            case 7:
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

    private Command Go(int phase, Command command)
    {
        _phase = phase;
        _wait = 0;
        return command;
    }

    private Command Fail(string message)
    {
        Finished = true;
        return new Command(Step.Fail, null, message);
    }
}
