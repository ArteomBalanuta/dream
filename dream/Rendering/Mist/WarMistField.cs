namespace Dream.Rendering.Mist;

// The war mist's numbers, kept in one pure place so the shader constants and the tests agree. The
// shader (warMistFragment.glsl) repeats these values; WarMistStructuralTests pins both sides.
//
// The mist is a stack of N translucent horizontal slices. The player's surroundings stay clear
// (nothing inside ClearRadius), the veil thickens to its full density by FullRadius, and it is
// thinner over ground the player has already seen. A slice's alpha is chosen so the N slices
// composed together (1 - (1 - a)^N) give the target density.
public static class WarMistField
{
    public const float ClearRadius = 3.75f;
    public const float FullRadius = 8.0f;
    public const float UnexploredDensity = 0.88f;
    public const float ExploredDensity = 0.58f;
    public const float MaxTargetDensity = 0.92f;
    public const float NoiseModMin = 0.70f;
    public const float NoiseModMax = 1.30f;

    // The quads' half extent around the focus, in metres. Not clipped to the map.
    public const float HalfExtent = 26f;

    // The map's floor top (WorldCursorPicker.FloorTop); the slice heights are offsets above it.
    public const float FloorTop = 1f;

    // The reference stack for six slices, bottom first: metres above the floor top.
    private static readonly float[] ReferenceHeights = [0.10f, 0.60f, 1.20f, 1.90f, 2.70f, 3.60f];

    // The wind the veil drifts along, its speed in metres per second, and how many noise tiles one
    // metre covers for the large billows and the small curls. The shader repeats the two scales.
    public const float WindX = 0.8f;
    public const float WindZ = 0.6f;
    public const float WindSpeed = 0.3f;
    public const double BigScale = 1d / 30d;
    public const double DetailScale = 1d / 11d;

    // Writes the per-slice texture offsets for the clock into dest (4 floats per slice, MaxSlices
    // slices): xy shift the large noise, zw the small one, each wrapped to [0, 1) in double, the
    // noise texture being tileable at 1. A slice's layers drift at 1.0x (bottom) to 1.3x (top) of
    // the wind speed and start from their own phase, so the stack parallaxes. Keeping the clock and
    // the wrap in double stops the drift from losing precision as the scene ages.
    public static void SliceOffsets(double seconds, int sliceCount, float[] dest)
    {
        int count = Math.Clamp(sliceCount, WarMistSettings.MinSlices, WarMistSettings.MaxSlices);
        for (int i = 0; i < WarMistSettings.MaxSlices; i++)
        {
            double layer01 = Math.Min(i, count - 1) / (double)Math.Max(count - 1, 1);
            double travel = WindSpeed * (1d + 0.3d * layer01) * seconds;
            double driftX = WindX * travel;
            double driftZ = WindZ * travel;
            double phaseX = i * 0.37d;
            double phaseZ = i * 0.61d;
            dest[i * 4] = Fract(driftX * BigScale + phaseX);
            dest[i * 4 + 1] = Fract(driftZ * BigScale + phaseZ);
            dest[i * 4 + 2] = Fract(-driftZ * 0.6d * DetailScale - phaseX);
            dest[i * 4 + 3] = Fract(driftX * 0.6d * DetailScale - phaseZ);
        }
    }

    private static float Fract(double value) => (float)(value - Math.Floor(value));

    // 0 inside ClearRadius, 1 from FullRadius out, smooth between.
    public static float Veil(float distance) => SmoothStep(ClearRadius, FullRadius, distance);

    // The composed density at a point: 0 inside the clear radius, otherwise the unexplored or explored
    // density (mixed by explored01) scaled by the noise modulation, clamped below full opacity.
    public static float TargetDensity(float distance, float explored01, float noiseMod)
    {
        float baseDensity = UnexploredDensity + (ExploredDensity - UnexploredDensity) * Math.Clamp(explored01, 0f, 1f);
        float target = Veil(distance) * baseDensity * Math.Clamp(noiseMod, NoiseModMin, NoiseModMax);
        return Math.Clamp(target, 0f, MaxTargetDensity);
    }

    // The alpha of one slice of sliceCount that composes to target.
    public static float SliceAlpha(float target, int sliceCount)
    {
        float clamped = Math.Clamp(target, 0f, MaxTargetDensity);
        return 1f - MathF.Pow(1f - clamped, 1f / sliceCount);
    }

    // The composed alpha of sliceCount slices of the given alpha.
    public static float Compose(float sliceAlpha, int sliceCount) => 1f - MathF.Pow(1f - sliceAlpha, sliceCount);

    // Absolute slice heights (floor top included), bottom first, padded to MaxSlices entries (the
    // shader's uniform array size). The reference stack is resampled linearly for other counts.
    public static float[] CreateSliceHeights(int sliceCount)
    {
        int count = Math.Clamp(sliceCount, WarMistSettings.MinSlices, WarMistSettings.MaxSlices);
        float[] heights = new float[WarMistSettings.MaxSlices];
        for (int i = 0; i < heights.Length; i++)
        {
            int index = Math.Min(i, count - 1);
            float t = index / (float)(count - 1) * (ReferenceHeights.Length - 1);
            int lower = (int)MathF.Floor(t);
            int upper = Math.Min(lower + 1, ReferenceHeights.Length - 1);
            float height = ReferenceHeights[lower] + (ReferenceHeights[upper] - ReferenceHeights[lower]) * (t - lower);
            heights[i] = FloorTop + height;
        }

        return heights;
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
