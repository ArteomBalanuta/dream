using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Presentation;

namespace Dream.Tests.Client.Presentation;

// Wave T2b: where held lights sit.
public sealed class HeldLightAnchorTests
{
    [Fact]
    public void FirstPerson_IsRightBelowAndAheadOfTheCamera()
    {
        Vector3 camera = new(10f, 2f, -3f);
        Vector3 front = -Vector3.UnitZ;
        Vector3 right = Vector3.UnitX;
        Vector3 up = Vector3.UnitY;

        Vector3 anchor = HeldLightAnchor.FirstPerson(camera, front, right, up);

        AssertClose(new Vector3(10.25f, 1.85f, -3.4f), anchor);
    }

    [Fact]
    public void Body_IsAtHandHeight_AheadAndToTheLeft()
    {
        // Facing -Z, right is +X (facing × up), as Camera.Right: the left is -X of the pose.
        Vector3 anchor = HeldLightAnchor.Body(new Vector3(1f, 0f, 1f), -Vector3.UnitZ);
        AssertClose(new Vector3(0.88f, 0.15f, 0.65f), anchor);

        // Facing +X, right is +Z, so the left is -Z.
        anchor = HeldLightAnchor.Body(Vector3.Zero, Vector3.UnitX);
        AssertClose(new Vector3(0.35f, 0.15f, -0.12f), anchor);
    }

    [Fact]
    public void Body_FlattensAndNormalizesTheFacing()
    {
        Vector3 flat = HeldLightAnchor.Body(Vector3.Zero, Vector3.UnitX);
        Vector3 pitchedAndLong = HeldLightAnchor.Body(Vector3.Zero, new Vector3(5f, -3f, 0f));
        AssertClose(flat, pitchedAndLong);
    }

    [Fact]
    public void Body_WithNoHorizontalFacing_SitsStraightAboveThePose()
    {
        Vector3 pose = new(2f, 3f, 4f);
        AssertClose(pose + new Vector3(0f, 0.15f, 0f), HeldLightAnchor.Body(pose, Vector3.Zero));
        AssertClose(pose + new Vector3(0f, 0.15f, 0f), HeldLightAnchor.Body(pose, Vector3.UnitY));
        AssertClose(pose + new Vector3(0f, 0.15f, 0f), HeldLightAnchor.Body(pose, new Vector3(float.NaN, 0f, 1f)));
    }

    private static void AssertClose(Vector3 expected, Vector3 actual)
    {
        Assert.True((expected - actual).Length < 1e-5f, $"Expected {expected}, got {actual}.");
    }
}
