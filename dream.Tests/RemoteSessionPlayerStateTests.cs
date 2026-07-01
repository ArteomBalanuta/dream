using OpenTK.Mathematics;
using Xunit;

public class RemoteSessionPlayerStateTests
{
    [Fact]
    public void ApplyCommand_UpdatesFacingAndHorizontalPosition()
    {
        RemoteSessionPlayerState state = new(
            actorId: 2001,
            sessionId: new PlayerSessionId("session-b"),
            position: new Vector3(4f, 1.5f, 4f));

        state.ApplyCommand(
            new PlayerCommandFrame(
                new PlayerSessionId("session-b"),
                Sequence: 7,
                PlayerModeSwitchIntent.None,
                new PlayerMoveIntent(ForwardAxis: 1f, StrafeAxis: 0f),
                new PlayerLookIntent(YawDelta: 3f, PitchDelta: 0f),
                PrimaryUsePressed: false),
            new PlayerSimulationTuning(
                EyeHeightOffset: 0.35f,
                MouseSensitivity: 2.5f,
                MoveSpeed: 6f,
                NoclipSpeed: 150f,
                JumpVelocity: 5f),
            deltaTime: SimulationClock.FixedDeltaSeconds);

        Assert.NotEqual(new Vector3(4f, 1.5f, 4f), state.Position);
        Assert.NotEqual(-Vector3.UnitZ, state.Facing);
        Assert.Equal("session-b", state.SessionId.Value);
    }

    [Fact]
    public void ApplyCommand_UpdatesInteractionModeFromRequestedMode()
    {
        RemoteSessionPlayerState state = new(
            actorId: 2001,
            sessionId: new PlayerSessionId("session-b"),
            position: new Vector3(4f, 1.5f, 4f));

        state.ApplyCommand(
            new PlayerCommandFrame(
                new PlayerSessionId("session-b"),
                Sequence: 8,
                new PlayerModeSwitchIntent(PlayerInteractionMode.Combat),
                PlayerMoveIntent.None,
                PlayerLookIntent.None,
                PrimaryUsePressed: false),
            new PlayerSimulationTuning(
                EyeHeightOffset: 0.35f,
                MouseSensitivity: 2.5f,
                MoveSpeed: 6f,
                NoclipSpeed: 150f,
                JumpVelocity: 5f),
            deltaTime: SimulationClock.FixedDeltaSeconds);

        Assert.Equal(PlayerInteractionMode.Combat, state.InteractionMode);
    }

    [Fact]
    public void ApplyCommand_UsesClientFacingForMovementDirection()
    {
        RemoteSessionPlayerState state = new(
            actorId: 2001,
            sessionId: new PlayerSessionId("session-b"),
            position: new Vector3(4f, 1.5f, 4f));

        state.ApplyCommand(
            new PlayerCommandFrame(
                new PlayerSessionId("session-b"),
                Sequence: 9,
                PlayerModeSwitchIntent.None,
                new PlayerMoveIntent(ForwardAxis: 1f, StrafeAxis: 0f),
                new PlayerLookIntent(YawDelta: 0f, PitchDelta: 0f, Facing: Vector3.UnitX),
                PrimaryUsePressed: false),
            new PlayerSimulationTuning(
                EyeHeightOffset: 0.35f,
                MouseSensitivity: 0.01f,
                MoveSpeed: 6f,
                NoclipSpeed: 150f,
                JumpVelocity: 5f),
            deltaTime: SimulationClock.FixedDeltaSeconds);

        Assert.True(state.Position.X > 4f);
        Assert.Equal(4f, state.Position.Z, 3);
        Assert.Equal(1f, state.Facing.X, 3);
        Assert.Equal(0f, state.Facing.Z, 3);
    }
}
