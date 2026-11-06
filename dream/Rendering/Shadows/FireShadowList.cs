using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OpenTK.Mathematics;

namespace Dream.Rendering.Shadows;

// Fixed-size std140 vec4[4] array: one entry per casting fire.
[InlineArray(ShadowSettings.MaxCastingFireCount)]
public struct FireShadowVector4Slots
{
    private Vector4 _element0;
}

// Fixed-size std140 vec4[16] array: one entry per (slot, face) atlas tile.
[InlineArray(ShadowSettings.MaxCastingFireCount * ShadowSettings.FacesPerLight)]
public struct FireShadowVector4Tiles
{
    private Vector4 _element0;
}

// Mirrors the std140 FireShadowList uniform block in shaders/fireShadows.glsl member for member (25 x vec4 = 400 bytes),
// the way FrameLighting mirrors lighting.glsl and LightList mirrors pointLights.glsl. Bound at binding point 3
// (0 is FrameLighting, 1 is LightList, 2 is BoundaryMistFrame). Pure data: FromFrame builds it from a ShadowFrame
// with no GL, so the layout and the tile arithmetic are testable.
[StructLayout(LayoutKind.Sequential)]
public struct FireShadowList
{
    public const int SizeInBytes = 16 + 2 * ShadowSettings.MaxCastingFireCount * 16 + ShadowSettings.MaxCastingFireCount * ShadowSettings.FacesPerLight * 16; // 400
    public const int BindingPoint = 3;
    public const string BlockName = "FireShadowList";

    // The faces of one fire, in the order of their atlas tiles (ShadowMapTargets.TileOrigin's face argument).
    // Face f looks down FaceForward(f) with +Y up (Matrix4.LookAt), which is what the GLSL face selection assumes.
    public const int FacePlusX = 0;
    public const int FaceMinusX = 1;
    public const int FacePlusZ = 2;
    public const int FaceMinusZ = 3;

    // The lookup's PCF kernel: four taps at +-TapOffsetTexels, each a hardware 2x2 comparison, so a tap reaches
    // TapOffsetTexels + 1 texel from the sample. The slope bias of the lookup is that reach in texels (a tilted
    // receiver's stored depth differs from the centre's by up to reach x texel x gradient), the same derivation
    // ShadowSettings.SlopeBiasTexels documents for the sun map. Mirrored by the FireShadow* constants in fireShadows.glsl.
    public const float TapOffsetTexels = 0.5f;
    public const float SlopeBiasTexels = TapOffsetTexels + 1f;

    public int Count; private int _pad0, _pad1, _pad2;   // ivec4 uFireShadowCount (x = active slots 0..4)
    public FireShadowVector4Slots Light;                 // offset 16: xyz flickered world position, w LightList index as a float
    public FireShadowVector4Slots Range;                 // offset 80: x near, y far, z gutter-shrunk tile scale, w strength
    public FireShadowVector4Tiles Tile;                  // offset 144: per (slot * 4 + face) xy tile origin in atlas UV, zw tile size in atlas UV

    public static Vector3 FaceForward(int face) => face switch
    {
        FacePlusX => Vector3.UnitX,
        FaceMinusX => -Vector3.UnitX,
        FacePlusZ => Vector3.UnitZ,
        FaceMinusZ => -Vector3.UnitZ,
        _ => throw new ArgumentOutOfRangeException(nameof(face)),
    };

    // The tile of (slot, face) as atlas UV: xy origin, zw size. Computed from ShadowMapTargets.TileOrigin, the one
    // definition of the layout, so the block can never disagree with the pixel tile the caster pass rendered into.
    public static Vector4 TileRect(int slot, int face, int tileSide, int atlasResolution)
    {
        (int x, int y) = ShadowMapTargets.TileOrigin(slot, face, tileSide);
        float inverse = 1f / atlasResolution;
        return new Vector4(x * inverse, y * inverse, tileSide * inverse, tileSide * inverse);
    }

    // The fraction of a tile the 90 degree face cone fills: the rendered FOV is widened so the cone lands inside the
    // tile minus its gutter on both sides (ShadowSettings.FaceFovRadians), hence tan(45 deg) / tan(fov / 2).
    public static float InnerScale(int tileSide, int gutter) => (tileSide - 2f * gutter) / tileSide;

    public static FireShadowList FromFrame(in ShadowFrame frame) =>
        FromFrame(in frame, ShadowSettings.TileSide, ShadowSettings.AtlasResolution, ShadowSettings.TileGutter);

    public static FireShadowList FromFrame(in ShadowFrame frame, int tileSide, int atlasResolution, int gutter)
    {
        FireShadowList list = default;
        int count = Math.Clamp(frame.FireCount, 0, ShadowSettings.MaxCastingFireCount);
        list.Count = count;
        float scale = InnerScale(tileSide, gutter);
        for (int slot = 0; slot < count; slot++)
        {
            FireShadowSlot fire = frame.Fires[slot];
            list.Light[slot] = new Vector4(fire.Position, fire.LightIndex);
            list.Range[slot] = new Vector4(fire.Near, fire.Far, scale, fire.Strength);
            for (int face = 0; face < ShadowSettings.FacesPerLight; face++)
            {
                list.Tile[slot * ShadowSettings.FacesPerLight + face] = TileRect(slot, face, tileSide, atlasResolution);
            }
        }

        return list;
    }
}
