using OpenTK.Windowing.GraphicsLibraryFramework;
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

    private static PlayerCommandFrame Held(params Keys[] keys) =>
        PlayerController.CaptureCommandFrame(new PlayerSessionId("local-player-1"), 1, key => Array.IndexOf(keys, key) >= 0, allowGameplayInput: true);

    [Fact]
    public void CaptureCommandFrame_WasdMoveNothing()
    {
        PlayerCommandFrame frame = Held(Keys.W, Keys.A, Keys.S, Keys.D);

        Assert.Equal(0f, frame.MoveIntent.ForwardAxis);
        Assert.Equal(0f, frame.MoveIntent.StrafeAxis);
        Assert.Equal(0f, frame.MoveIntent.VerticalAxis);
        Assert.False(frame.MoveIntent.JumpPressed);
        Assert.Equal(0f, frame.LookIntent.YawDelta);
        Assert.False(frame.PrimaryUsePressed);
    }

    [Fact]
    public void CaptureCommandFrame_SpaceJumpsAndRisesInNoclip_ControlDescends()
    {
        PlayerCommandFrame jump = Held(Keys.Space);
        Assert.True(jump.MoveIntent.JumpPressed);
        Assert.Equal(1f, jump.MoveIntent.VerticalAxis);

        PlayerCommandFrame down = Held(Keys.LeftControl);
        Assert.Equal(-1f, down.MoveIntent.VerticalAxis);
        Assert.False(down.MoveIntent.JumpPressed);
    }

    [Fact]
    public void CaptureCommandFrame_ZeroTogglesNoclip_OneAndTwoPickTheMode()
    {
        Assert.True(Held(Keys.D0).MoveIntent.ToggleNoclipPressed);
        Assert.Equal(PlayerInteractionMode.Building, Held(Keys.D1).RequestedMode.Mode);
        Assert.Equal(PlayerInteractionMode.Combat, Held(Keys.D2).RequestedMode.Mode);
        Assert.Null(Held().RequestedMode.Mode);
    }

    [Fact]
    public void CaptureCommandFrame_BlockedInput_IsNeutral()
    {
        PlayerCommandFrame frame = PlayerController.CaptureCommandFrame(
            new PlayerSessionId("local-player-1"), 1, _ => true, allowGameplayInput: false);

        Assert.False(frame.MoveIntent.JumpPressed);
        Assert.False(frame.MoveIntent.ToggleNoclipPressed);
        Assert.Equal(0f, frame.MoveIntent.VerticalAxis);
        Assert.Null(frame.RequestedMode.Mode);
    }
}
