using OpenTK.Mathematics;
using Xunit;

public class PlayerControllerTests
{
    [Fact]
    public void CreateMovementCommand_Flattens_Camera_Axes_And_Normalizes_Diagonal_Input()
    {
        MovementCommand command = PlayerController.CreateMovementCommand(
            cameraFront: new Vector3(0f, 2f, -2f),
            cameraRight: new Vector3(3f, 0f, 0f),
            moveForward: true,
            moveBackward: false,
            moveLeft: false,
            moveRight: true,
            jumpPressed: false,
            freezePressed: false,
            allowGameplayInput: true);

        AssertVectorEqual(new Vector3(1f, 0f, -1f).Normalized(), command.WorldDirection);
        Assert.False(command.JumpPressed);
        Assert.False(command.FreezePressed);
    }

    [Fact]
    public void CreateMovementCommand_WhenGameplayInputIsBlocked_ReturnsEmptyCommand()
    {
        MovementCommand command = PlayerController.CreateMovementCommand(
            cameraFront: -Vector3.UnitZ,
            cameraRight: Vector3.UnitX,
            moveForward: true,
            moveBackward: false,
            moveLeft: false,
            moveRight: false,
            jumpPressed: true,
            freezePressed: true,
            allowGameplayInput: false);

        Assert.Equal(Vector3.Zero, command.WorldDirection);
        Assert.False(command.JumpPressed);
        Assert.False(command.FreezePressed);
    }

    [Fact]
    public void CreateMovementCommand_WhenCameraHasNoWorldPlaneComponent_ReturnsZeroDirection()
    {
        MovementCommand command = PlayerController.CreateMovementCommand(
            cameraFront: Vector3.UnitY,
            cameraRight: Vector3.Zero,
            moveForward: true,
            moveBackward: false,
            moveLeft: false,
            moveRight: false,
            jumpPressed: true,
            freezePressed: true,
            allowGameplayInput: true);

        Assert.Equal(Vector3.Zero, command.WorldDirection);
        Assert.False(float.IsNaN(command.WorldDirection.X));
        Assert.False(float.IsNaN(command.WorldDirection.Y));
        Assert.False(float.IsNaN(command.WorldDirection.Z));
        Assert.True(command.JumpPressed);
        Assert.True(command.FreezePressed);
    }

    [Fact]
    public void CreateInteractionCommand_RequiresGameplayInputAndPrimaryUsePressed()
    {
        Assert.True(PlayerController.CreateInteractionCommand(primaryUsePressed: true, allowGameplayInput: true).PlaceCubePressed);
        Assert.False(PlayerController.CreateInteractionCommand(primaryUsePressed: true, allowGameplayInput: false).PlaceCubePressed);
        Assert.False(PlayerController.CreateInteractionCommand(primaryUsePressed: false, allowGameplayInput: true).PlaceCubePressed);
    }

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

    private static void AssertVectorEqual(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 5);
        Assert.Equal(expected.Y, actual.Y, 5);
        Assert.Equal(expected.Z, actual.Z, 5);
    }
}
