using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Input;
using Dream.Simulation.Players;

namespace Dream.Tests.Simulation.Players;


public class PlayerMovementResolverTests
{
    [Fact]
    public void Resolve_Noclip_Uses_Full_Camera_Forward_And_Vertical_Input()
    {
        Camera camera = new(new Vector3(5.5f, 1.85f, 5.5f));

        (Vector3 direction, float verticalDirection) = PlayerMovementResolver.Resolve(
            camera,
            noclipEnabled: true,
            moveForward: true,
            moveBackward: false,
            moveLeft: false,
            moveRight: false,
            moveUp: true,
            moveDown: false);

        Assert.True(direction.Z < 0f);
        Assert.Equal(1f, verticalDirection);
    }

    [Fact]
    public void Resolve_NormalMovement_Flattens_Forward_Vector_And_Ignores_Vertical()
    {
        Camera camera = new(new Vector3(5.5f, 1.85f, 5.5f));

        (Vector3 direction, float verticalDirection) = PlayerMovementResolver.Resolve(
            camera,
            noclipEnabled: false,
            moveForward: true,
            moveBackward: false,
            moveLeft: false,
            moveRight: false,
            moveUp: true,
            moveDown: true);

        Assert.Equal(0f, direction.Y);
        Assert.Equal(0f, verticalDirection);
    }

    [Fact]
    public void CreateMovementCommand_Walking_Flattens_Camera_Axes_And_Normalizes_Diagonal_Intent()
    {
        Camera camera = CreateCamera(new Vector3(0f, 1f, -1f));

        MovementCommand command = PlayerMovementResolver.CreateMovementCommand(
            camera,
            noclipEnabled: false,
            new PlayerMoveIntent(1f, 1f, 1f, JumpPressed: false, FreezePressed: false, ToggleNoclipPressed: false));

        Vector3 expected = new Vector3(1f, 0f, -1f).Normalized();
        AssertVectorEqual(expected, command.WorldDirection);
        Assert.Equal(0f, command.WorldDirection.Y);
        Assert.Equal(0f, command.VerticalDirection);
    }

    [Fact]
    public void CreateMovementCommand_Noclip_Uses_Full_Camera_Forward_And_Vertical_Axis()
    {
        Vector3 facing = new Vector3(0f, 1f, -1f).Normalized();
        Camera camera = CreateCamera(facing);

        MovementCommand command = PlayerMovementResolver.CreateMovementCommand(
            camera,
            noclipEnabled: true,
            new PlayerMoveIntent(1f, 0f, -1f, JumpPressed: false, FreezePressed: false, ToggleNoclipPressed: false));

        AssertVectorEqual(facing, command.WorldDirection);
        Assert.Equal(-1f, command.VerticalDirection);
    }

    [Fact]
    public void CreateMovementCommand_Neutral_Axes_Give_Zero_Direction_And_Copy_Button_Intents()
    {
        Camera camera = CreateCamera(-Vector3.UnitZ);

        MovementCommand command = PlayerMovementResolver.CreateMovementCommand(
            camera,
            noclipEnabled: false,
            new PlayerMoveIntent(0f, 0f, 0f, JumpPressed: true, FreezePressed: true, ToggleNoclipPressed: true));

        Assert.Equal(Vector3.Zero, command.WorldDirection);
        Assert.False(float.IsNaN(command.WorldDirection.X));
        Assert.Equal(0f, command.VerticalDirection);
        Assert.True(command.JumpPressed);
        Assert.True(command.FreezePressed);
        Assert.True(command.ToggleNoclipPressed);
    }

    [Fact]
    public void CreateMovementCommand_Matches_PlayerController_Forwarder_And_Reference_Bit_For_Bit()
    {
        float[] yaws = [-3.1f, -MathHelper.PiOver2, -0.7f, 0f, 0.3f, 1f, MathHelper.PiOver2, 2.4f, MathHelper.Pi];
        float[] pitches = [-MathHelper.PiOver2, -1.2f, -0.4f, 0f, 0.25f, 0.9f, MathHelper.PiOver2];
        float[] axes = [-1f, -0.5f, 0f, 0.5f, 1f];
        bool[] flags = [false, true];
        int comparisons = 0;

        foreach (float yaw in yaws)
        {
            foreach (float pitch in pitches)
            {
                Camera camera = CreateCamera(new Vector3(
                    MathF.Cos(pitch) * MathF.Cos(yaw),
                    MathF.Sin(pitch),
                    MathF.Cos(pitch) * MathF.Sin(yaw)));

                foreach (bool noclip in flags)
                {
                    foreach (float forward in axes)
                    {
                        foreach (float strafe in axes)
                        {
                            foreach (float vertical in axes)
                            {
                                foreach (bool button in flags)
                                {
                                    PlayerMoveIntent intent = new(forward, strafe, vertical, button, !button, button);

                                    MovementCommand resolved = PlayerMovementResolver.CreateMovementCommand(camera, noclip, intent);
                                    MovementCommand forwarded = PlayerController.CreateMovementCommand(camera, noclip, intent);
                                    MovementCommand reference = CreateReferenceMovementCommand(camera, noclip, intent);

                                    AssertBitIdentical(reference, resolved);
                                    AssertBitIdentical(forwarded, resolved);
                                    comparisons++;
                                }
                            }
                        }
                    }
                }
            }
        }

        Assert.Equal(yaws.Length * pitches.Length * flags.Length * axes.Length * axes.Length * axes.Length * flags.Length, comparisons);
    }

    // Copy of the pre-move PlayerController maths; client prediction and server reconciliation depend on it staying bit-identical.
    private static MovementCommand CreateReferenceMovementCommand(Camera camera, bool noclipEnabled, PlayerMoveIntent intent)
    {
        Vector3 forward = noclipEnabled
            ? camera.Front.Normalized()
            : ReferenceFlatten(camera.Front);
        Vector3 right = ReferenceFlatten(camera.Right);
        Vector3 movementDirection = (forward * intent.ForwardAxis) + (right * intent.StrafeAxis);
        if (movementDirection != Vector3.Zero)
        {
            movementDirection = movementDirection.Normalized();
        }

        float verticalDirection = noclipEnabled ? intent.VerticalAxis : 0f;
        return new MovementCommand(
            movementDirection,
            verticalDirection,
            intent.JumpPressed,
            intent.FreezePressed,
            intent.ToggleNoclipPressed);
    }

    private static Vector3 ReferenceFlatten(Vector3 direction)
    {
        Vector3 flattened = new(direction.X, 0f, direction.Z);
        return flattened == Vector3.Zero ? Vector3.Zero : flattened.Normalized();
    }

    private static Camera CreateCamera(Vector3 facing)
    {
        Camera camera = new(Vector3.Zero);
        camera.SetFacing(facing);
        return camera;
    }

    private static void AssertBitIdentical(MovementCommand expected, MovementCommand actual)
    {
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.WorldDirection.X), BitConverter.SingleToInt32Bits(actual.WorldDirection.X));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.WorldDirection.Y), BitConverter.SingleToInt32Bits(actual.WorldDirection.Y));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.WorldDirection.Z), BitConverter.SingleToInt32Bits(actual.WorldDirection.Z));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.VerticalDirection), BitConverter.SingleToInt32Bits(actual.VerticalDirection));
        Assert.Equal(expected.JumpPressed, actual.JumpPressed);
        Assert.Equal(expected.FreezePressed, actual.FreezePressed);
        Assert.Equal(expected.ToggleNoclipPressed, actual.ToggleNoclipPressed);
    }

    private static void AssertVectorEqual(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 5);
        Assert.Equal(expected.Y, actual.Y, 5);
        Assert.Equal(expected.Z, actual.Z, 5);
    }
}
