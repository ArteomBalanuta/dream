using Xunit;
using Dream.Client.Input;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;

namespace Dream.Tests.Client.Input;


public class PlayerControllerTests
{
    [Fact]
    public void CreateCommandFrame_BindsSessionSequenceAndGameplayIntents()
    {
        PlayerCommandFrame frame = PlayerController.CreateCommandFrame(
            sessionId: new PlayerSessionId("local-player-1"),
            sequence: 12,
            allowGameplayInput: true,
            forwardAxis: 1f,
            strafeAxis: -1f,
            verticalAxis: 0f,
            jumpPressed: true,
            freezePressed: false,
            toggleNoclipPressed: true,
            yawDelta: 4f,
            pitchDelta: -3f,
            primaryUsePressed: true,
            selectBuildingModePressed: false,
            selectCombatModePressed: true);

        Assert.Equal("local-player-1", frame.SessionId.Value);
        Assert.Equal((ulong)12, frame.Sequence);
        Assert.Equal(1f, frame.MoveIntent.ForwardAxis);
        Assert.Equal(-1f, frame.MoveIntent.StrafeAxis);
        Assert.True(frame.MoveIntent.JumpPressed);
        Assert.True(frame.MoveIntent.ToggleNoclipPressed);
        Assert.Equal(4f, frame.LookIntent.YawDelta);
        Assert.Equal(-3f, frame.LookIntent.PitchDelta);
        Assert.True(frame.PrimaryUsePressed);
        Assert.Equal(PlayerInteractionMode.Combat, frame.RequestedMode.Mode);
    }

    [Fact]
    public void CreateCommandFrame_WhenGameplayInputBlocked_ProducesNeutralIntents()
    {
        PlayerCommandFrame frame = PlayerController.CreateCommandFrame(
            sessionId: new PlayerSessionId("local-player-1"),
            sequence: 5,
            allowGameplayInput: false,
            forwardAxis: 1f,
            strafeAxis: 1f,
            verticalAxis: 1f,
            jumpPressed: true,
            freezePressed: true,
            toggleNoclipPressed: true,
            yawDelta: 8f,
            pitchDelta: 2f,
            primaryUsePressed: true,
            selectBuildingModePressed: true,
            selectCombatModePressed: true);

        Assert.Equal(0f, frame.MoveIntent.ForwardAxis);
        Assert.Equal(0f, frame.MoveIntent.StrafeAxis);
        Assert.Equal(0f, frame.MoveIntent.VerticalAxis);
        Assert.False(frame.MoveIntent.JumpPressed);
        Assert.False(frame.MoveIntent.FreezePressed);
        Assert.False(frame.MoveIntent.ToggleNoclipPressed);
        Assert.Equal(0f, frame.LookIntent.YawDelta);
        Assert.Equal(0f, frame.LookIntent.PitchDelta);
        Assert.False(frame.PrimaryUsePressed);
        Assert.Null(frame.RequestedMode.Mode);
    }
}
