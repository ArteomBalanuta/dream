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

    private static void AssertVectorEqual(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 5);
        Assert.Equal(expected.Y, actual.Y, 5);
        Assert.Equal(expected.Z, actual.Z, 5);
    }
}
