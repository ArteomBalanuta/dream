using OpenTK.Mathematics;
using Xunit;

public class ThirdPersonCameraRigTests
{
    [Fact]
    public void Resolve_Returns_First_Person_Clone_When_Mode_Is_First_Person()
    {
        Camera firstPerson = new(new Vector3(5f, 1.85f, 5f));
        firstPerson.SetFacing(-Vector3.UnitZ);

        Camera resolved = ThirdPersonCameraRig.Resolve(
            firstPerson,
            new Vector3(5f, 1.5f, 5f),
            CameraViewMode.FirstPerson);

        Assert.Equal(firstPerson.Position, resolved.Position);
        Assert.Equal(firstPerson.Front, resolved.Front);
    }

    [Fact]
    public void Resolve_Places_Camera_Behind_Player_And_Looks_At_Target()
    {
        Camera firstPerson = new(new Vector3(5f, 1.85f, 5f));
        firstPerson.SetFacing(-Vector3.UnitZ);

        Camera resolved = ThirdPersonCameraRig.Resolve(
            firstPerson,
            new Vector3(5f, 1.5f, 5f),
            CameraViewMode.ThirdPerson);

        Assert.True(resolved.Position.Z > 5f);
        Assert.True(resolved.Position.Y > 1.85f);
        Assert.True(resolved.Front.Z < 0f);
    }

    [Fact]
    public void Resolve_Uses_Horizontal_Facing_For_Stable_Chase_Offset()
    {
        Camera firstPerson = new(new Vector3(5f, 1.85f, 5f));
        firstPerson.SetFacing(new Vector3(1f, -0.8f, 0f));

        Camera resolved = ThirdPersonCameraRig.Resolve(
            firstPerson,
            new Vector3(5f, 1.5f, 5f),
            CameraViewMode.ThirdPerson);

        Assert.True(resolved.Position.X < 5f);
        Assert.InRange(resolved.Position.Y, 2f, 5f);
        Assert.True(resolved.Front.X > 0f);
    }
}
