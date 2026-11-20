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
