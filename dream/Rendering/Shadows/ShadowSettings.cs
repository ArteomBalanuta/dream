using System.Globalization;
using Dream.Simulation.World;
using OpenTK.Mathematics;

namespace Dream.Rendering.Shadows;

// The shadow-map system's knobs and constants (docs/ledger/wave-shadow-maps.md). Env knobs follow the house
// pattern (DREAM_GROUND_SCATTER, DREAM_NIGHT_BRIGHTNESS): a pure Parse/Resolve function that tests drive, and a
// static that reads the environment once. Every derived constant is derived in code and pinned by a test
// relation, never restated as a number in a comment.
public static class ShadowSettings
{
    public const string EnabledEnvironmentVariable = "DREAM_SHADOWS";
    public const string ResolutionEnvironmentVariable = "DREAM_SHADOW_RESOLUTION";
    public const string FiresEnvironmentVariable = "DREAM_SHADOW_FIRES";

    public static readonly int[] AllowedSunResolutions = [512, 1024, 2048, 4096];
    public const int DefaultSunResolution = 2048;

    // The default is 4 BY EXPLICIT DECISION (the design suggested 2): the user asked for quality and the GPU spends
    // only a fraction of a millisecond on the world pass, so four casting fires are affordable on the GPU. The frame
    // is CPU-bound on draw submission, though, and every casting fire adds a depth pass of four faces. If the measured
    // CPU cost is unacceptable this is the FIRST knob to turn down (DREAM_SHADOW_FIRES=2, then 0).
    public const int DefaultCastingFireCount = 4;
    public const int MaxCastingFireCount = 4;

    // Sampler units: 0-4 are the material textures and the terrain samplers, 5 and 6 were free.
    public const int SunTextureUnit = 5;
    public const int AtlasTextureUnit = 6;
    public const string SunMapUniform = "uSunShadowMap";
    public const string AtlasUniform = "uFireShadowAtlas";

    // The fire atlas: a GridSide x GridSide grid of tiles. Slots x faces must fill it exactly (4 lights x 4 faces).
    public const int GridSide = 4;
    public const int FacesPerLight = 4;
    public const int AtlasResolution = 2048;
    public const int TileGutter = 4;
    public const int SunTaps = 9;
    public const int FireTaps = 4;

    // The receiver box that only translates with the focus (the isometric camera is fixed): a ground footprint of
    // about 16 x 23 units plus relief and the tallest props, with margin.
    public const float ReceiverSide = 40f;
    public const float ReceiverHeight = 26f;

    // The sun map is centred ahead of the focus along the camera's view azimuth, where more of the visible ground is
    // (the screen shows more ground in front of the focus than behind it).
    public const float ReceiverCentreOffset = 3f;

    // Sun elevation sine below which CasterExtension stops growing. Equal to SkyModel.LightFadeAbove (private there;
    // pinned equal by a reflection test), below which the sun intensity is fading out anyway.
    public const float MinElevationSin = 0.06f;

    // A caster the visible pass never draws must not cast either, so the extension is capped at twice the draw distance.
    public const float MaxCasterExtension = 2f * WorldVisibility.MaxRenderDistance;

    // Bias. NormalOffsetTexels scales with the texel by construction. ConstantBiasWorld is a fixed physical threshold.
    // The SLOPE term must scale with the texel too: the PCF kernel reaches a couple of texels sideways while the
    // reference depth stays at the centre, so a receiver tilted by tan(theta) to the light needs about (reach in
    // texels) x texel x tan of bias. A fixed world-unit slope bias (the design's 2 cm) is enough at the default 2048
    // map but leaves acne at 1024 and 512 (SunShadowLookupMirrorTests, per resolution). So the slope coefficient is
    // defined in TEXELS, calibrated to equal SlopeBiasWorld at the default resolution and linear in the texel beyond.
    public const float NormalOffsetTexels = 1.5f;
    public const float ConstantBiasWorld = 0.006f;
    public const float SlopeBiasWorld = 0.020f;
    public const float SlopeTanMax = 4f;
    public const float GrazingCosineFloor = 0.15f;

    public static float SlopeBiasTexels => SlopeBiasWorld / TexelWorldFor(DefaultSunResolution);

    // World bias per unit of tan(incidence) for a map whose texel is `texelWorld`, and its cap at SlopeTanMax.
    public static float SlopeBiasWorldFor(float texelWorld) => SlopeBiasTexels * texelWorld;

    public static float SlopeBiasMaxWorldFor(float texelWorld) => SlopeBiasWorldFor(texelWorld) * SlopeTanMax;

    // The UBO carries only constant-bias / depth-range (SunShadowParams.y) and 1 / resolution (.x). The shader forms
    // the slope term as y * (1 + SlopePerInverseResolution * x * tan): texel = OrthoSide * x, so
    // slope / constant = SlopeBiasTexels * OrthoSide * x / ConstantBiasWorld.
    public static float SlopePerInverseResolution => SlopeBiasTexels * OrthoSide / ConstantBiasWorld;

    public static bool Enabled { get; } = ParseEnabled(Environment.GetEnvironmentVariable(EnabledEnvironmentVariable));

    public static int SunResolution { get; } = ResolveSunResolution(Environment.GetEnvironmentVariable(ResolutionEnvironmentVariable));

    public static int CastingFireCount { get; } = ResolveCastingFireCount(Environment.GetEnvironmentVariable(FiresEnvironmentVariable));

    public static int TileSide => AtlasResolution / GridSide;

    // Light-space footprint of a ReceiverSide x ReceiverSide x ReceiverHeight box at sun elevation theta is
    // S * (S cos(theta) + H sin(theta)), maximised over theta at sqrt(S^2 + H^2). Constant, so the world texel is constant.
    public static float OrthoSide => MathF.Sqrt(ReceiverSide * ReceiverSide + ReceiverHeight * ReceiverHeight);

    public static float TexelWorldFor(int resolution) => OrthoSide / resolution;

    public static float TexelWorld => TexelWorldFor(SunResolution);

    // A caster h above the receiver casts along the light from h / sin(elevation) away.
    public static float CasterExtensionFor(float elevationSin) =>
        MathF.Min(ReceiverHeight / MathF.Max(elevationSin, MinElevationSin), MaxCasterExtension);

    // Each fire face is widened so the 90 degree cone lands inside the tile minus its gutter on both sides.
    public static float FaceFovRadians =>
        2f * MathF.Atan(MathF.Tan(MathHelper.PiOver4) * TileSide / (TileSide - 2f * TileGutter));

    public static bool ParseEnabled(string? value) => value?.Trim() != "0";

    /// <summary>Snaps to the nearest allowed resolution, rounding down on a tie (like MsaaSettings.Sanitize).</summary>
    public static int SanitizeSunResolution(int value)
    {
        int best = AllowedSunResolutions[0];
        foreach (int candidate in AllowedSunResolutions)
        {
            if (Math.Abs(candidate - value) < Math.Abs(best - value))
            {
                best = candidate;
            }
        }

        return best;
    }

    public static int ResolveSunResolution(string? value) =>
        TryParseInt(value, out int parsed) ? SanitizeSunResolution(parsed) : DefaultSunResolution;

    public static int ResolveCastingFireCount(string? value) =>
        TryParseInt(value, out int parsed) ? Math.Clamp(parsed, 0, MaxCastingFireCount) : DefaultCastingFireCount;

    private static bool TryParseInt(string? value, out int parsed)
    {
        parsed = 0;
        return !string.IsNullOrWhiteSpace(value)
            && int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
    }
}
