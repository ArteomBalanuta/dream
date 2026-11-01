using Dream.Client.Input;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using Xunit;

namespace Dream.Tests.Client.Input;

public class CursorChangeLookHoldoffTests
{
    private static PlayerCommandFrame Frame(float yaw, float forward = 0f) => new(
        new PlayerSessionId("session-a"),
        1,
        PlayerModeSwitchIntent.None,
        new PlayerMoveIntent(forward, 0f, 0f, JumpPressed: false, FreezePressed: false, ToggleNoclipPressed: false),
        new PlayerLookIntent(YawDelta: yaw, PitchDelta: 0f),
        PrimaryUsePressed: false);

    [Fact]
    public void PlayFromTheStart_PassesLookThrough()
    {
        CursorChangeLookHoldoff filter = new();

        Assert.Equal(4f, filter.Filter(Frame(4f), allowGameplayInput: true).LookIntent.YawDelta);
    }

    [Fact]
    public void AfterTheWindowsClose_LookStaysZeroForTheHoldoff_ThenFlowsAgain()
    {
        CursorChangeLookHoldoff filter = new();
        _ = filter.Filter(Frame(0f), allowGameplayInput: false);

        for (int n = 0; n < CursorChangeLookHoldoff.Frames; n++)
        {
            // The cursor jump of the mode change shows up as a large delta in any of these frames.
            Assert.Equal(PlayerLookIntent.None, filter.Filter(Frame(900f), allowGameplayInput: true).LookIntent);
        }

        Assert.Equal(2f, filter.Filter(Frame(2f), allowGameplayInput: true).LookIntent.YawDelta);
    }

    [Fact]
    public void Movement_IsNeverHeldBack()
    {
        CursorChangeLookHoldoff filter = new();
        _ = filter.Filter(Frame(0f), allowGameplayInput: false);

        Assert.Equal(1f, filter.Filter(Frame(900f, forward: 1f), allowGameplayInput: true).MoveIntent.ForwardAxis);
    }

    [Fact]
    public void WhileTheWindowsAreOpen_TheFrameIsReturnedUntouched_AndTheHoldoffRestarts()
    {
        CursorChangeLookHoldoff filter = new();
        _ = filter.Filter(Frame(0f), allowGameplayInput: false);
        _ = filter.Filter(Frame(5f), allowGameplayInput: true);

        // Closed and reopened in the middle of a holdoff: the full holdoff applies again.
        PlayerCommandFrame open = filter.Filter(Frame(7f), allowGameplayInput: false);
        Assert.Equal(7f, open.LookIntent.YawDelta);
        for (int n = 0; n < CursorChangeLookHoldoff.Frames; n++)
        {
            Assert.Equal(PlayerLookIntent.None, filter.Filter(Frame(900f), allowGameplayInput: true).LookIntent);
        }
    }
}
