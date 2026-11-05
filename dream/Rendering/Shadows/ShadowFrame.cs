using System.Runtime.CompilerServices;
using Dream.Rendering.Lighting;
using OpenTK.Mathematics;

namespace Dream.Rendering.Shadows;

// One casting fire of the frame: which entry of the frame's LightList it is, its FLICKERED world position (the depth
// map's origin moves with the flame), the face planes and the strength of its shadow.
public struct FireShadowSlot
{
    public int LightIndex;
    public Vector3 Position;
    public float Near;
    public float Far;
    public float Strength;
}

[InlineArray(ShadowSettings.MaxCastingFireCount)]
public struct FireShadowSlots
{
    private FireShadowSlot _first;
}

// The per-frame shadow description. Defined by workstream D1, filled by D2 (the caster pass) and uploaded by D3
// (the fire block). Plain data, no GL, no allocation: it is a value type with an inline array of slots.
public struct ShadowFrame
{
    // World -> light CLIP space (SunShadowVolume.Build). The caster pass renders the sun map through this.
    public Matrix4 SunLightViewProjection;

    // World -> shadow TEXTURE space (SunShadowVolume.ToTextureSpace). This is what the UBO carries.
    public Matrix4 SunTextureMatrix;

    public float SunTexelWorld;
    public float SunDepthRange;

    // x 1 / resolution, y constant bias / depth range, z normal offset (world), w strength; the UBO's SunShadowParams.
    public Vector4 SunParams;

    public int FireCount;
    public FireShadowSlots Fires;

    public readonly bool SunActive => SunParams.W > 0f;

    // Nothing shadowed: strength 0 everywhere, so the shaders return visibility 1 (and DREAM_SHADOWS=0 looks as before).
    public static ShadowFrame None => default;

    /// <summary>Builds the sun half of a frame for the dominant light. <paramref name="strength"/> 0 keeps it inert.</summary>
    public static ShadowFrame ForSun(Vector3 focus, Vector3 lightDirection, float elevationSin, int resolution, float strength)
    {
        (Matrix4 lightViewProjection, float texel, float depthRange) = SunShadowVolume.Build(focus, lightDirection, elevationSin, resolution);
        ShadowFrame frame = default;
        frame.SunLightViewProjection = lightViewProjection;
        frame.SunTextureMatrix = SunShadowVolume.ToTextureSpace(in lightViewProjection);
        frame.SunTexelWorld = texel;
        frame.SunDepthRange = depthRange;
        frame.SunParams = new Vector4(
            1f / resolution,
            SunShadowVolume.ConstantBiasDepth(depthRange),
            SunShadowVolume.NormalOffsetWorld(texel),
            strength);
        return frame;
    }

    /// <summary>Writes the sun members into the frame's lighting block (the world range; ForViewModel zeroes them).</summary>
    public readonly void WriteSunTo(ref FrameLighting lighting)
    {
        lighting.SunShadowMatrix = SunTextureMatrix;
        lighting.SunShadowParams = SunParams;
    }
}
