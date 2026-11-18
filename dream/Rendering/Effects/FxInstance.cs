using System.Runtime.InteropServices;
using OpenTK.Mathematics;

namespace Dream.Rendering.Effects;

// Atlas cells (particles/particles.png, 8 x 4 cells of 64 px, frame 0 top-left, rows downward).
// Each family has four variants, picked per particle by adding 0..3 to the first frame.
public enum FxFrame : byte
{
    Spark0 = 0,
    Ember0 = 4,
    Dust0 = 8,
    Ash0 = 12,
    Halo = 16,
    HaloCore = 17,
    Flame0 = 18,
    Blob = 22,
    // Skill casts: one flat crescent, two bolt variants (add 0..1), one flat annulus.
    Arc = 23,
    Bolt0 = 24,
    Ring = 26
}

public static class FxAtlas
{
    public const int Columns = 8;
    public const int Rows = 4;
    public const int CellPixels = 64;
    public const int MaxMipLevel = 2;
    public const string RelativePath = "particles/particles.png";
}

public static class FxLimits
{
    public const int MaxInstances = 2048;
    public const int HaloBudget = 128;
    public const int ShadowBudget = 32;
    public const int ParticleCapacity = MaxInstances - HaloBudget - ShadowBudget;
}

// One billboard as the GPU reads it: three per-instance attributes, divisor 1, stride 36.
// Color is premultiplied RGBA with R in the low byte; A is how much the quad occludes what is
// behind it, so A = 0 is pure additive light. Lifetime fade scales all four channels.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct FxInstance
{
    public const int Stride = 36;
    public const int PositionHalfSizeLocation = 0;
    public const int ColorLocation = 1;
    public const int RotationFrameBiasLocation = 2;
    public const int ColorOffset = 16;
    public const int RotationOffset = 20;

    public Vector3 Position;
    public float HalfSize;
    public uint Color;
    public float Rotation;
    public float Frame;
    // World units toward the camera, so a halo at a cube's centre is not cut by the cube's depth.
    public float DepthBias;
    // 0 or 1 is a square camera-facing billboard, above 1 stretches the quad along its local +X,
    // and a negative value (GroundAligned) lays it flat on the XZ plane.
    public float Stretch;
    public const float GroundAligned = -1f;

    public static uint PackColor(float r, float g, float b, float a)
    {
        return ToByte(r) | (ToByte(g) << 8) | (ToByte(b) << 16) | (ToByte(a) << 24);
    }

    private static uint ToByte(float value)
    {
        // NaN fails both comparisons and lands on 0.
        return value >= 1f ? 255u : value > 0f ? (uint)(value * 255f + 0.5f) : 0u;
    }
}
