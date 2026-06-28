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
