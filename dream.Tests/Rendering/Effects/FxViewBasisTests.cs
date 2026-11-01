using Dream.Rendering.Effects;
using Dream.Simulation.Players;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Effects;

// The billboards face the camera through axes read from the view matrix, so they must agree with
// the Camera's own basis in every pose the game renders from.
public class FxViewBasisTests
{
    public static TheoryData<string, float, float, float, float, float, float> Poses => new()
    {
        { "first person", 5f, 1.6f, 5f, 0.2f, 0.05f, -1f },
        { "first person looking down", 5f, 1.6f, 5f, 0.3f, -0.9f, -0.3f },
        { "third person behind the player", 5f, 3.2f, 9f, 0f, -0.25f, -1f },
        { "isometric-like", 40f, 38f, 40f, -1f, -1.15f, -1f },
    };

    [Theory]
    [MemberData(nameof(Poses))]
    public void Basis_Matches_The_Camera_Axes(string pose, float px, float py, float pz, float fx, float fy, float fz)
    {
        Camera camera = new(new Vector3(px, py, pz));
        camera.SetFacing(new Vector3(fx, fy, fz));

        FxViewBasis basis = FxViewBasis.FromView(camera.GetViewMatrix());

        AssertClose(camera.Right, basis.Right, pose);
        AssertClose(camera.Up, basis.Up, pose);
        AssertClose(-camera.Front, basis.Back, pose);
    }

    [Fact]
    public void Basis_Is_Orthonormal_And_Right_Handed()
    {
        Camera camera = new(new Vector3(-12f, 7f, 3f));
        camera.SetFacing(new Vector3(0.6f, -0.3f, 0.7f));

        FxViewBasis basis = FxViewBasis.FromView(camera.GetViewMatrix());

        Assert.InRange(basis.Right.Length, 0.999f, 1.001f);
        Assert.InRange(basis.Up.Length, 0.999f, 1.001f);
        Assert.InRange(basis.Back.Length, 0.999f, 1.001f);
        Assert.InRange(Vector3.Dot(basis.Right, basis.Up), -1e-4f, 1e-4f);
        AssertClose(basis.Back, Vector3.Cross(basis.Right, basis.Up), "handedness");
    }

    [Fact]
    public void Translation_Does_Not_Change_The_Basis()
    {
        Camera near = new(Vector3.Zero);
        Camera far = new(new Vector3(900f, -40f, 1200f));
        near.SetFacing(new Vector3(0.3f, 0.2f, -1f));
        far.SetFacing(new Vector3(0.3f, 0.2f, -1f));

        FxViewBasis a = FxViewBasis.FromView(near.GetViewMatrix());
        FxViewBasis b = FxViewBasis.FromView(far.GetViewMatrix());

        AssertClose(a.Right, b.Right, "right");
        AssertClose(a.Up, b.Up, "up");
        AssertClose(a.Back, b.Back, "back");
    }

    private static void AssertClose(Vector3 expected, Vector3 actual, string label)
    {
        Assert.True((expected - actual).Length < 1e-4f, $"{label}: expected {expected}, got {actual}.");
    }
}
