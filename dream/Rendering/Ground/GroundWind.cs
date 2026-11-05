using OpenTK.Mathematics;

namespace Dream.Rendering.Ground;

// Ambient sway of the ground scatter: which asset classes move, how far, and the uWind value the vertex
// shader (shaders/cubeVertex.glsl) takes. The amplitude is the displacement of the asset's tip in world
// units (the player is 1.20 tall); everything that is not a plant is 0 and never touches the uniform.
// The table is C# keyed on GroundAssetClass, not a manifest field, so the dream.ground/1 schema is unchanged.
public static class GroundWind
{
    public readonly record struct Sway(float Amplitude, float Stiffness)
    {
        public bool IsStill => Amplitude <= 0f;
    }

    public static Sway For(GroundAssetClass assetClass) => assetClass switch
    {
        GroundAssetClass.Tree => new Sway(0.10f, 2.5f),
        GroundAssetClass.Sapling => new Sway(0.08f, 2.0f),
        GroundAssetClass.Bush => new Sway(0.05f, 1.8f),
        GroundAssetClass.Grass => new Sway(0.045f, 1.3f),
        GroundAssetClass.Flower => new Sway(0.04f, 1.3f),
        GroundAssetClass.Mushroom => new Sway(0.006f, 2.0f),
        GroundAssetClass.Leaves => new Sway(0.03f, 1.2f),
        GroundAssetClass.Twigs => new Sway(0.02f, 1.5f),
        _ => default,
    };

    // The sway is sin(1.7 t + p) and sin(2.9 t + 1.7 p). Both are whole turns after exactly 20 pi seconds
    // (1.7 * 20 pi = 34 pi, 2.9 * 20 pi = 58 pi), so the clock may be reduced modulo this with no visible jump,
    // whatever the phase. (The boundary mist's 6400 s wrap has no such property for the sway.)
    public const double ClockPeriodSeconds = 20d * Math.PI;

    // The sway clock for FrameTime.x: the raw clock reduced into [0, ClockPeriodSeconds) in double precision.
    public static float WrapClock(double rawSeconds)
    {
        double r = rawSeconds % ClockPeriodSeconds;
        return (float)(r < 0d ? r + ClockPeriodSeconds : r);
    }

    // The uWind value for an asset: x = amplitude / height^stiffness so that the shader's
    // x * pow(aPos.y, y) reaches the amplitude at the asset's height. Vector2.Zero when the asset stands still
    // (or has no usable height).
    public static Vector2 Uniform(GroundAssetClass assetClass, float assetHeight)
    {
        Sway sway = For(assetClass);
        if (sway.IsStill || !float.IsFinite(assetHeight) || assetHeight <= 0f)
        {
            return Vector2.Zero;
        }

        return new Vector2(sway.Amplitude / MathF.Pow(assetHeight, sway.Stiffness), sway.Stiffness);
    }

    // CPU mirror of the vertex shader's sway: the world-space displacement magnitude (signed along the
    // wind direction) of a vertex at local height `y`, for the wind uniform `wind`, at clock `seconds`
    // with the instance phase `phase` and the instance's scale (the shader's length(instanceModel[1].xyz):
    // gusts scale with the plant, so a half-size sapling sways half as far). A zero uniform returns exactly 0.
    public static float Displacement(Vector2 wind, float y, float seconds, float phase, float instanceScale = 1f)
    {
        if (wind.X <= 0f)
        {
            return 0f;
        }

        float sway = (0.7f * MathF.Sin((seconds * 1.7f) + phase)) + (0.3f * MathF.Sin((seconds * 2.9f) + (phase * 1.7f)));
        return sway * wind.X * MathF.Pow(MathF.Max(y, 0f), wind.Y) * instanceScale;
    }
}

// Tracks the uWind value last handed to a program and forwards only changes, so a run of trees costs
// one upload and the reset after the scatter is one more. `apply` does the actual GL call; tests pass a
// recorder, which is what proves the reset happens.
public sealed class WindUniform
{
    private readonly Action<Vector2> _apply;
    private Vector2 _current = Vector2.Zero;

    public WindUniform(Action<Vector2> apply)
    {
        _apply = apply;
    }

    public Vector2 Current => _current;

    public void Set(Vector2 wind)
    {
        if (wind == _current)
        {
            return;
        }

        _current = wind;
        _apply(wind);
    }

    public void Reset() => Set(Vector2.Zero);
}
