using OpenTK.Mathematics;

namespace Dream.Rendering.Effects;

// The camera axes the billboards face, read from the view matrix the frame actually draws with, so
// every camera mode (first person, third person, isometric) gets the right quads without the pass
// knowing which one is active. Back points from the scene toward the eye.
public readonly record struct FxViewBasis(Vector3 Right, Vector3 Up, Vector3 Back)
{
    // OpenTK row-vector LookAt: the rotation sits transposed in the upper 3x3, so each camera
    // axis is a column of the view matrix.
    public static FxViewBasis FromView(in Matrix4 view)
    {
        return new FxViewBasis(
            new Vector3(view.M11, view.M21, view.M31),
            new Vector3(view.M12, view.M22, view.M32),
            new Vector3(view.M13, view.M23, view.M33));
    }
}
