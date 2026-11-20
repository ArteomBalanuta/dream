using System.Globalization;

namespace Dream.Rendering.Post;

/// <summary>
/// The scene post chain's switches and constants. Everything here is resolved once, at renderer
/// construction, from the environment; nothing changes while the window lives.
/// </summary>
/// <remarks>
/// The chain is off by default in no sense: it is ON unless <c>DREAM_POST=0</c>, because the
/// shoulder it applies is the identity below <see cref="Knee"/> and the frame is almost entirely
/// below it. The switch exists so a capture can be taken with the pass out of the way when
/// something has to be attributed to it, and so a driver that cannot give a complete float
/// framebuffer has somewhere to fall back to.
/// </remarks>
public static class PostSettings
{
    public const string EnabledVariable = "DREAM_POST";
    public const string BloomStrengthVariable = "DREAM_BLOOM";
    public const string ShadowNeutraliseVariable = "DREAM_SHADOW_NEUTRALISE";

    /// <summary>
    /// Where the highlight shoulder starts. Mirrors <c>DreamToneKnee</c> in postTone.glsl, which is
    /// the value that actually runs; this copy exists so the C# side can reason about it and so a
    /// test can prove the two have not drifted. Below it the resolve is the identity.
    /// </summary>
    public const float Knee = 0.80f;

    /// <summary>
    /// How much of the blurred overflow is added back. The bloom source is zero everywhere below
    /// the knee, so this scales a glow that only exists around light that was going to clip.
    /// </summary>
    public const float DefaultBloomStrength = 0.35f;

    /// <summary>
    /// Below this max-channel value the resolve pulls a pixel's chroma toward neutral. Mirrors
    /// <c>DreamShadowNeutraliseEdge</c> in postTone.glsl. Far under <see cref="Knee"/>.
    /// </summary>
    public const float ShadowNeutraliseEdge = 0.08f;

    /// <summary>
    /// How much of the darkest pixels' chroma is removed. Measured on night and noon captures:
    /// mean saturation 0.544 / 0.378 to 0.327 / 0.327, inside the 0.311-0.371 band measured off
    /// the reference art, with every value-derived gate number unchanged.
    /// </summary>
    public const float DefaultShadowNeutraliseStrength = 0.80f;

    /// <summary>The bloom chain's divisor against the scene resolution, in each axis.</summary>
    public const int BloomDownscale = 4;

    /// <summary>The smallest bloom texture side; below this the blur taps leave the texture.</summary>
    public const int MinimumBloomSide = 8;

    public static bool ResolveEnabled(string? environmentValue) =>
        !(environmentValue != null && environmentValue.Trim() == "0");

    public static bool ResolveEnabledFromEnvironment() =>
        ResolveEnabled(Environment.GetEnvironmentVariable(EnabledVariable));

    /// <summary>Clamped to [0, 2]; an unparseable or absent value gives the default.</summary>
    public static float ResolveBloomStrength(string? environmentValue)
    {
        if (!string.IsNullOrWhiteSpace(environmentValue)
            && float.TryParse(environmentValue.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
            && float.IsFinite(parsed))
        {
            return Math.Clamp(parsed, 0f, 2f);
        }

        return DefaultBloomStrength;
    }

    public static float ResolveBloomStrengthFromEnvironment() =>
        ResolveBloomStrength(Environment.GetEnvironmentVariable(BloomStrengthVariable));

    /// <summary>Clamped to [0, 1]; an unparseable or absent value gives the default. 0 is grade-off.</summary>
    public static float ResolveShadowNeutraliseStrength(string? environmentValue)
    {
        if (!string.IsNullOrWhiteSpace(environmentValue)
            && float.TryParse(environmentValue.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
            && float.IsFinite(parsed))
        {
            return Math.Clamp(parsed, 0f, 1f);
        }

        return DefaultShadowNeutraliseStrength;
    }

    public static float ResolveShadowNeutraliseStrengthFromEnvironment() =>
        ResolveShadowNeutraliseStrength(Environment.GetEnvironmentVariable(ShadowNeutraliseVariable));

    /// <summary>
    /// The shadow neutralisation, in C#, exactly as <c>dreamToneNeutraliseShadows</c> computes it:
    /// c + (max - c) * w, in the subtractive form so the max channel is returned bit for bit.
    /// </summary>
    public static OpenTK.Mathematics.Vector3 NeutraliseShadows(OpenTK.Mathematics.Vector3 c, float strength)
    {
        float m = MathF.Max(MathF.Max(c.X, c.Y), c.Z);
        float x = Math.Clamp(m / ShadowNeutraliseEdge, 0f, 1f);
        float smooth = x * x * (3f - (2f * x));
        float w = (1f - smooth) * strength;
        if (w <= 0f)
        {
            return c;
        }

        return new OpenTK.Mathematics.Vector3(
            c.X + ((m - c.X) * w), c.Y + ((m - c.Y) * w), c.Z + ((m - c.Z) * w));
    }

    /// <summary>
    /// The tone shoulder, in C#, exactly as postTone.glsl computes it. Used by the tests that pin
    /// the curve's two load-bearing properties - identity below the knee, and a slope of 1 at the
    /// knee so there is no seam - without a GL context.
    /// </summary>
    public static float Rolloff(float channelMax)
    {
        if (channelMax <= Knee)
        {
            return channelMax;
        }

        const float head = 1f - Knee;
        float t = channelMax - Knee;
        return Knee + (head * (t / (t + head)));
    }

    /// <summary>The part of a value the shoulder is about to take: zero at or below the knee.</summary>
    public static float Overflow(float channelMax) => channelMax <= Knee ? 0f : channelMax - Knee;
}
