using OpenTK.Mathematics;
using Xunit;

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
}
