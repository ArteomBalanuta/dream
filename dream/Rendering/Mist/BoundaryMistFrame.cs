using System.Runtime.InteropServices;
using OpenTK.Mathematics;

namespace Dream.Rendering.Mist;

// What the scene tells the renderer about the boundary mist each frame: the map size, the clock
// and the switch. The scene sets it before the world pass; Enabled false (DREAM_BOUNDARY_MIST=0)
// uploads strength 0, which makes every shader skip the mist.
public readonly record struct BoundaryMistInput(float Width, float Depth, float TimeSeconds, bool Enabled);

// Mirrors the std140 BoundaryMist uniform block in shaders/boundaryMist.glsl member for member.
[StructLayout(LayoutKind.Explicit, Size = SizeInBytes)]
public struct BoundaryMistFrame
{
    public const int SizeInBytes = 48;
    public const int BindingPoint = 2; // FrameLighting is 0, LightList 1
    public const string BlockName = "BoundaryMist";

    // Height of the floor plane the mist is measured on: the top of a floor cell (y 0..1).
    public const float FloorTopY = 1f;

    [FieldOffset(0)] public Vector4 Bounds; // minX, minZ, maxX (map width), maxZ (map depth)
    [FieldOffset(16)] public Vector4 Params; // x strength (1 on, 0 off), y time s (wrapped), z floor top y, w ramp start
    [FieldOffset(32)] public Vector4 View; // xyz unit camera front, w 0

    // The world range: the map rectangle, the wrapped clock and the camera's view direction.
    public static BoundaryMistFrame ForWorld(in BoundaryMistInput input, Vector3 cameraFront)
    {
        Vector3 front = cameraFront.LengthSquared > 1e-8f ? cameraFront.Normalized() : -Vector3.UnitZ;
        return new BoundaryMistFrame
        {
            Bounds = new Vector4(0f, 0f, input.Width, input.Depth),
            Params = new Vector4(
                input.Enabled && input.Width > 0f && input.Depth > 0f ? 1f : 0f,
                BoundaryMistField.WrapTime(input.TimeSeconds),
                FloorTopY,
                BoundaryMistField.RampStart),
            View = new Vector4(front, 0f),
        };
    }

    // The view-model range: strength 0. The view model draws in eye space, where the world-space
    // map rectangle means nothing, and the hands must never be misted.
    public static BoundaryMistFrame ForViewModel(in BoundaryMistInput input, Vector3 cameraFront)
    {
        BoundaryMistFrame frame = ForWorld(in input, cameraFront);
        frame.Params.X = 0f;
        return frame;
    }
}
