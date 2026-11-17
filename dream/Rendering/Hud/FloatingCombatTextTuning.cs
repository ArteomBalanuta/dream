namespace Dream.Rendering.Hud;

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
        SizeMultiplier: 2.35f,
        MotionSpeedMultiplier: 0.60f,
        CriticalDepthMultiplier: 5.00f);

    public FloatingCombatTextTuning Clamp()
    {
        FloatingCombatTextTuning fallback = Default;
        return new FloatingCombatTextTuning(
            Sanitize(SizeMultiplier, MinSizeMultiplier, MaxSizeMultiplier, fallback.SizeMultiplier),
            Sanitize(MotionSpeedMultiplier, MinMotionSpeedMultiplier, MaxMotionSpeedMultiplier, fallback.MotionSpeedMultiplier),
            Sanitize(CriticalDepthMultiplier, MinCriticalDepthMultiplier, MaxCriticalDepthMultiplier, fallback.CriticalDepthMultiplier));
    }

    // Math.Clamp passes NaN straight through, so a non-finite setting would poison the combat-text
    // layout instead of being rejected. NaN and +/-Infinity both fall back to the default, matching
    // the sanitising the other config readers do (DesktopClientConfig.SanitizeVolume).
    private static float Sanitize(float value, float min, float max, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
