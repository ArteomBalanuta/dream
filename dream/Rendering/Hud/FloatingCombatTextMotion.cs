using Dream.Simulation.Combat;

namespace Dream.Rendering.Hud;

public static class FloatingCombatTextMotion
{
    public const float BurstDuration = 0.15f;
    public const float FloatStartTime = 0.15f;
    public const float FadeStartTime = 0.60f;
    public const float Lifetime = 1.0f;
    public const float MaxHeight = 1.0f;
    public const float MaxDepthOffset = 0.20f;
    public const float InitialScale = 0.2f;
    public const float PeakScaleMultiplier = 1.1f;

    // Classic (sprite) curve. Times are on a 1.4 s timeline that equals real seconds at the default
    // motion speed (ClassicReferenceMotionSpeed) and stretches with slower motion.
    public const float ClassicLifetime = 1.4f;
    public const float ClassicPopSeconds = 0.15f;
    public const float ClassicFadeStartFraction = 0.75f;
    public const float ClassicReferenceMotionSpeed = 0.6f;
    public const float ClassicRiseExponent = 2.2f;
    public const float ClassicRiseBodies = 3.4f;
    public const float ClassicReferenceHeight = 1080f;
    public const float ClassicReferenceSizeMultiplier = 2.35f;
    public const float ClassicSpawnFillPixels = 68f;
    public const float ClassicFloorFillPixels = 24f;
    // One body (B) is the fill height plus rim: the fill is 0.8 of it.
    public const float ClassicBodyPixels = ClassicSpawnFillPixels / 0.8f;
    public const float ClassicRegularSpawnScale = 0.85f;
    public const float ClassicJitterPixels = 3f;

    public static float ClassicSpawnScale(FloatingCombatTextKind kind) =>
        kind == FloatingCombatTextKind.Critical ? 1.0f : ClassicRegularSpawnScale;

    // Fill height in pixels of a number of the given age: starts at its spawn size and shrinks to the
    // floor within ClassicPopSeconds (quadratic ease-out), at 1080p tall and size factor 1.
    public static float ClassicFillPixels(float ageSeconds, FloatingCombatTextKind kind)
    {
        float spawn = ClassicSpawnFillPixels * ClassicSpawnScale(kind);
        float remaining = 1f - Math.Clamp(ageSeconds / ClassicPopSeconds, 0f, 1f);
        return ClassicFloorFillPixels + ((spawn - ClassicFloorFillPixels) * remaining * remaining);
    }

    // Rise in pixels at 1080p tall and size factor 1: ease-out over the whole lifetime.
    public static float ClassicRisePixels(float ageSeconds)
    {
        float t = Math.Clamp(ageSeconds / ClassicLifetime, 0f, 1f);
        return ClassicRiseBodies * ClassicBodyPixels * (1f - MathF.Pow(1f - t, ClassicRiseExponent));
    }

    public static float ClassicAlpha(float ageSeconds)
    {
        float t = ageSeconds / ClassicLifetime;
        if (t <= ClassicFadeStartFraction)
        {
            return 1f;
        }

        return t >= 1f ? 0f : (1f - t) / (1f - ClassicFadeStartFraction);
    }

    public static float ResolveLifetimeSeconds(float motionSpeedMultiplier)
    {
        float resolvedMotionSpeed = Math.Clamp(motionSpeedMultiplier, 0.01f, 100f);
        return Lifetime / resolvedMotionSpeed;
    }

    public static float EvaluateScale(float ageSeconds, float nativeScale)
    {
        if (ageSeconds <= 0f)
        {
            return InitialScale * nativeScale;
        }

        if (ageSeconds >= BurstDuration)
        {
            return nativeScale;
        }

        float normalized = ageSeconds / BurstDuration;
        if (normalized < 0.5f)
        {
            float burstUp = normalized / 0.5f;
            return Lerp(InitialScale * nativeScale, PeakScaleMultiplier * nativeScale, burstUp);
        }

        float settle = (normalized - 0.5f) / 0.5f;
        return Lerp(PeakScaleMultiplier * nativeScale, nativeScale, settle);
    }

    public static float EvaluateVerticalOffset(float ageSeconds, float maxHeight = MaxHeight)
    {
        if (ageSeconds <= 0f)
        {
            return 0f;
        }

        float normalized = ageSeconds / FadeStartTime;
        normalized = Math.Clamp(normalized, 0f, 1f);
        return maxHeight * (1f - ((1f - normalized) * (1f - normalized)));
    }

    public static float EvaluateDepthOffset(float ageSeconds, float maxDepthOffset = MaxDepthOffset)
    {
        if (ageSeconds <= 0f || ageSeconds >= BurstDuration)
        {
            return 0f;
        }

        float normalized = ageSeconds / BurstDuration;
        if (normalized < 0.5f)
        {
            return Lerp(0f, maxDepthOffset, normalized / 0.5f);
        }

        return Lerp(maxDepthOffset, 0f, (normalized - 0.5f) / 0.5f);
    }

    public static float EvaluateAlpha(float ageSeconds)
    {
        if (ageSeconds <= FadeStartTime)
        {
            return 1f;
        }

        if (ageSeconds >= Lifetime)
        {
            return 0f;
        }

        float normalized = (ageSeconds - FadeStartTime) / (Lifetime - FadeStartTime);
        return 1f - normalized;
    }

    private static float Lerp(float start, float end, float amount) => start + ((end - start) * amount);
}
