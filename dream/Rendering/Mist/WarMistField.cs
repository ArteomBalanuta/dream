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
    public const float ClearRadius = 6.5f;
    public const float FullRadius = 12f;
    public const float UnexploredDensity = 0.72f;
    public const float ExploredDensity = 0.30f;
    public const float MaxTargetDensity = 0.95f;
    public const float NoiseModMin = 0.6f;
    public const float NoiseModMax = 1.3f;

    // The quads' half extent around the focus, in metres, clipped to the map.
    public const float HalfExtent = 26f;

    // The map's floor top (WorldCursorPicker.FloorTop); the slice heights are offsets above it.
    public const float FloorTop = 1f;

    // The reference stack for six slices, bottom first: metres above the floor top.
    private static readonly float[] ReferenceHeights = [0.10f, 0.60f, 1.20f, 1.90f, 2.70f, 3.60f];

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
