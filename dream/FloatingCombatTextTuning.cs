public readonly record struct FloatingCombatTextTuning(
    float SizeMultiplier,
    float MotionSpeedMultiplier,
    float CriticalDepthMultiplier)
{
    public const float MinSizeMultiplier = 0.75f;
    public const float MaxSizeMultiplier = 4.00f;
    public const float MinMotionSpeedMultiplier = 0.25f;
    public const float MaxMotionSpeedMultiplier = 4.00f;
    public const float MinCriticalDepthMultiplier = 0.00f;
    public const float MaxCriticalDepthMultiplier = 6.00f;

    public static FloatingCombatTextTuning Default => new(
        SizeMultiplier: 1.65f,
        MotionSpeedMultiplier: 1.20f,
        CriticalDepthMultiplier: 2.50f);

    public FloatingCombatTextTuning Clamp()
    {
        return new FloatingCombatTextTuning(
            Math.Clamp(SizeMultiplier, MinSizeMultiplier, MaxSizeMultiplier),
            Math.Clamp(MotionSpeedMultiplier, MinMotionSpeedMultiplier, MaxMotionSpeedMultiplier),
            Math.Clamp(CriticalDepthMultiplier, MinCriticalDepthMultiplier, MaxCriticalDepthMultiplier));
    }
}
