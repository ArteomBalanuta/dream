namespace Dream.Rendering.Ui;

public static class UiScale
{
    public const float Step = 0.25f;
    public const float Min = 1f;
    public const float Max = 4f;

    // The design surface is 640 x 480 units. configured <= 0 (or not finite) is auto: the largest
    // multiple of 0.25 that fits both axes. Any other value snaps to the nearest 0.25 and is then
    // capped at the auto fit, because a larger scale pushes the windows under the bar. Both clamp
    // to 1..4. With no viewport (width or height <= 0) a configured value is only snapped.
    public static float Resolve(int width, int height, float configured)
    {
        bool hasViewport = width > 0 && height > 0;
        if (float.IsFinite(configured) && configured > 0f)
        {
            float snapped = Clamp(MathF.Round(configured / Step) * Step);
            return hasViewport ? MathF.Min(snapped, AutoFit(width, height)) : snapped;
        }

        return hasViewport ? AutoFit(width, height) : Min;
    }

    private static float AutoFit(int width, int height)
    {
        float fit = MathF.Min(height / 480f, width / 640f);
        return Clamp(MathF.Floor(fit / Step) * Step);
    }

    private static float Clamp(float v) => Math.Clamp(v, Min, Max);
}
