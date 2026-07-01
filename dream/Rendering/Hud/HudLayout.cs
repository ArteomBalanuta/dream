namespace Dream.Rendering.Hud;

public readonly record struct HudLayout(
    float CenterX,
    float CenterY,
    float ArmLength,
    float Gap)
{
    public static HudLayout CreateCrosshair(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(width, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(height, 0);

        float viewportWidth = width;
        float viewportHeight = height;
        float smallerAxis = Math.Min(viewportWidth, viewportHeight);

        float armLength = Math.Clamp(smallerAxis * 0.018f, 8f, 18f);
        float gap = Math.Clamp(smallerAxis * 0.0075f, 3f, 8f);

        return new HudLayout(
            viewportWidth / 2f,
            viewportHeight / 2f,
            armLength,
            gap);
    }

    public float[] ToVertexArray()
    {
        return
        [
            CenterX - Gap - ArmLength, CenterY,
            CenterX - Gap, CenterY,
            CenterX + Gap, CenterY,
            CenterX + Gap + ArmLength, CenterY,
            CenterX, CenterY - Gap - ArmLength,
            CenterX, CenterY - Gap,
            CenterX, CenterY + Gap,
            CenterX, CenterY + Gap + ArmLength,
        ];
    }
}
