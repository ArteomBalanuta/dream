using Xunit;
using Dream.Client.Input;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;

namespace Dream.Tests.Client.Input;

public class DeadInputPolicyTests
{
    private static PlayerCommandFrame HeldEverything(ulong sequence, bool allow)
    {
        return PlayerController.CreateCommandFrame(
            new PlayerSessionId("session-a"),
            sequence,
            allow,
            forwardAxis: 1f,
            strafeAxis: 1f,
            verticalAxis: 1f,
            jumpPressed: true,
            freezePressed: true,
            toggleNoclipPressed: true,
            yawDelta: 5f,
            pitchDelta: 5f,
            primaryUsePressed: true,
            selectBuildingModePressed: true,
            selectCombatModePressed: false);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void AllowGameplayInput_NeedsBothTheSettingAndALivePlayer(bool settings, bool alive, bool expected)
    {
        Assert.Equal(expected, DeadInputPolicy.AllowGameplayInput(settings, alive));
    }

    [Fact]
    public void AHeldKeyWhileDead_SendsNoMovementOrUse_AndTheCommandKeepsItsSequence()
    {
        PlayerCommandFrame frame = HeldEverything(42, DeadInputPolicy.AllowGameplayInput(true, localPlayerIsAlive: false));

        Assert.Equal(42UL, frame.Sequence);
        Assert.Equal(PlayerMoveIntent.None, frame.MoveIntent);
        Assert.Equal(PlayerLookIntent.None, frame.LookIntent);
        Assert.False(frame.PrimaryUsePressed);
        Assert.Null(frame.RequestedMode.Mode);
    }

    [Fact]
    public void TheFirstFrameAfterTheRespawn_DropsTheOneShotsHeldThroughTheDeath()
    {
        GameplayInputResumeFilter filter = new();
        PlayerCommandFrame Frame(ulong sequence, bool alive)
        {
            bool allow = DeadInputPolicy.AllowGameplayInput(true, alive);
            return filter.Filter(HeldEverything(sequence, allow), allow);
        }

        Assert.Equal(PlayerMoveIntent.None, Frame(1, alive: false).MoveIntent);
        PlayerCommandFrame resumed = Frame(2, alive: true);

        Assert.Equal(2UL, resumed.Sequence);
        Assert.Equal(1f, resumed.MoveIntent.ForwardAxis);
        Assert.False(resumed.MoveIntent.JumpPressed);
        Assert.False(resumed.PrimaryUsePressed);
        Assert.Equal(PlayerLookIntent.None, resumed.LookIntent);
    }
}
