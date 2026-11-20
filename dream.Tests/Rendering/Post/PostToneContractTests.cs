using System.Globalization;
using System.Text.RegularExpressions;
using Dream.Rendering.Post;
using Dream.Tests.Integration;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Post;

// The resolve applies TWO transforms, and this class pins both.
//
// The first is the highlight shoulder, and its three properties are pinned here rather than left to
// a capture to notice.
//
// 1. It is the IDENTITY below the knee. This is what makes the pass safe to add to a renderer whose
//    whole art catalogue - albedos, fog, tints, the character palette gates - was calibrated against
//    a hard clip at 1.0. Almost every pixel in a frame is under the knee and comes out of the shoulder
//    exactly as it went in. That is now true of VALUE only: see the second transform below.
// 2. Its slope at the knee is exactly 1, so there is no step and no band where the curve takes over.
// 3. It never reaches 1.0, so nothing clips however bright it gets. That is the point of the pass.
//
// The second is the shadow neutralisation, applied last. It is the identity in the MAX CHANNEL
// everywhere (bit for bit, so every value-derived number the look gate reads is unchanged) and the
// identity in chroma above 0.08; below 0.08 it pulls a pixel's channels toward its own max, which
// removes saturation and nothing else. It is NOT the identity for a dark pixel's colour, so "comes
// out exactly as it went in" holds for the max channel and for any pixel at or above 0.08, no more.
//
// And the C# copy must agree with the GLSL that actually runs, which the last test reads out of the
// shader rather than trusting a comment.
public class PostToneContractTests
{
    private const float Tolerance = 1e-6f;

    [Theory]
    [InlineData(0f)]
    [InlineData(0.01f)]
    [InlineData(0.25f)]
    [InlineData(0.42f)]
    [InlineData(0.5f)]
    [InlineData(0.79f)]
    [InlineData(PostSettings.Knee)]
    public void BelowTheKnee_TheCurveIsTheIdentity(float value)
    {
        Assert.Equal(value, PostSettings.Rolloff(value), 6);
        Assert.Equal(0f, PostSettings.Overflow(value), 6);
    }

    [Fact]
    public void AtTheKnee_TheSlopeIsOne_SoThereIsNoSeam()
    {
        const float h = 1e-4f;
        float below = (PostSettings.Rolloff(PostSettings.Knee) - PostSettings.Rolloff(PostSettings.Knee - h)) / h;
        float above = (PostSettings.Rolloff(PostSettings.Knee + h) - PostSettings.Rolloff(PostSettings.Knee)) / h;
        Assert.Equal(1f, below, 3);
        Assert.Equal(1f, above, 3);
    }

    [Theory]
    [InlineData(0.9f)]
    [InlineData(1f)]
    [InlineData(2f)]
    [InlineData(8f)]
    [InlineData(64f)]
    [InlineData(1000f)]
    public void AboveTheKnee_TheCurveRisesButNeverReachesOne(float value)
    {
        float mapped = PostSettings.Rolloff(value);
        Assert.True(mapped > PostSettings.Knee, $"{value} mapped to {mapped}, not past the knee");
        Assert.True(mapped < 1f, $"{value} mapped to {mapped}, which clips");
        Assert.True(mapped < value + Tolerance, $"{value} mapped UP to {mapped}");
    }

    [Fact]
    public void TheCurveIsMonotonic_SoAStrongerLightIsNeverDarker()
    {
        float previous = -1f;
        for (int i = 0; i <= 4000; i++)
        {
            float value = i * 0.005f;
            float mapped = PostSettings.Rolloff(value);
            Assert.True(mapped >= previous - Tolerance, $"the curve fell at {value}");
            previous = mapped;
        }
    }

    // The engine's own numbers. The 0.42 albedo ceiling the art is held to, times the peak gain of
    // about 2.2 measured beside a brazier, is 0.924: the ceiling exists precisely so that the
    // DIFFUSE term alone lands just under the clip. What it leaves is 0.076 of headroom for
    // everything that is added on top of diffuse - the specular lobe, the equipment rim, an
    // emissive - so in practice any of those put the pixel over 1.0 and flattened it. That is the
    // failure this pass removes.
    [Fact]
    public void TheEngineSPeakDiffuse_SitsOnTheShoulderWithRoomAboveIt()
    {
        const float albedoCeiling = 0.42f;
        const float peakGain = 2.2f;
        float diffuse = albedoCeiling * peakGain;

        // It was always meant to fit, and only just: above the knee, under the clip.
        Assert.InRange(diffuse, PostSettings.Knee, 1f);
        Assert.True(1f - diffuse < 0.1f, $"the headroom moved: {1f - diffuse}");

        // The sum that used to clip. A rim at 0.15 on top of peak diffuse is 1.074 - a flat white
        // patch before this pass, and now a value with room left above it.
        float withRim = diffuse + 0.15f;
        Assert.True(withRim > 1f, "the premise moved: that sum used to clip");
        float mapped = PostSettings.Rolloff(withRim);
        Assert.InRange(mapped, PostSettings.Knee, 1f);

        // And twice as much light still reads as brighter, which a clip could not say.
        Assert.True(PostSettings.Rolloff(withRim * 2f) > mapped + 0.01f);
    }

    [Fact]
    public void TheOverflowFeedingTheBloom_IsZeroBelowTheKnee_AndTheExcessAbove()
    {
        Assert.Equal(0f, PostSettings.Overflow(0.5f), 6);
        Assert.Equal(0f, PostSettings.Overflow(PostSettings.Knee), 6);
        Assert.Equal(0.2f, PostSettings.Overflow(PostSettings.Knee + 0.2f), 5);
    }

    // The C# above is a mirror; this reads the number the GPU actually uses.
    [Fact]
    public void TheShaderKnee_MatchesPostSettings()
    {
        string source = File.ReadAllText(
            Path.Combine(RepoRoot.Find(), "dream", "shaders", "postTone.glsl"));
        Match match = Regex.Match(source, @"const\s+float\s+DreamToneKnee\s*=\s*([0-9.]+)\s*;");
        Assert.True(match.Success, "postTone.glsl no longer declares DreamToneKnee");
        float shaderKnee = float.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.Equal(PostSettings.Knee, shaderKnee, 5);
    }

    // ---- Shadow neutralisation ----

    private static float Max(Vector3 c) => MathF.Max(MathF.Max(c.X, c.Y), c.Z);

    private static float Saturation(Vector3 c)
    {
        float m = Max(c);
        float n = MathF.Min(MathF.Min(c.X, c.Y), c.Z);
        return m <= 0f ? 0f : (m - n) / m;
    }

    private static IEnumerable<Vector3> DarkColourGrid()
    {
        float[] steps = [0f, 0.001f, 0.004f, 0.01f, 0.02f, 0.033f, 0.05f, 0.0625f, 0.079f, 0.08f, 0.1f, 0.4f, 0.8f, 1.5f];
        foreach (float r in steps)
        {
            foreach (float g in steps)
            {
                foreach (float b in steps)
                {
                    yield return new Vector3(r, g, b);
                }
            }
        }
    }

    // The property the whole grade rests on, asserted on the BITS: a tolerance would let an ULP
    // through, and an ULP is a flipped byte.
    [Fact]
    public void NeutraliseShadows_LeavesTheMaxChannelBitExact()
    {
        float[] strengths = [0f, 0.1f, 0.33f, 0.5f, 0.8f, 0.97f, 1f];
        int checkedCount = 0;
        foreach (Vector3 c in DarkColourGrid())
        {
            foreach (float strength in strengths)
            {
                Vector3 o = PostSettings.NeutraliseShadows(c, strength);
                Assert.Equal(BitConverter.SingleToInt32Bits(Max(c)), BitConverter.SingleToInt32Bits(Max(o)));
                checkedCount++;
            }
        }

        Assert.True(checkedCount > 10000);
    }

    // The synthetic negative: the obvious alternative, mixing toward luma, must FAIL the assertion
    // above, or the assertion could not tell the right form from the wrong one.
    private static Vector3 MixTowardLuma(Vector3 c, float strength)
    {
        float luma = (0.2126f * c.X) + (0.7152f * c.Y) + (0.0722f * c.Z);
        float x = Math.Clamp(Max(c) / PostSettings.ShadowNeutraliseEdge, 0f, 1f);
        float w = (1f - (x * x * (3f - (2f * x)))) * strength;
        return c + ((new Vector3(luma) - c) * w);
    }

    [Fact]
    public void TheMaxChannelAssertion_FailsForAMixTowardLuma()
    {
        bool moved = false;
        foreach (Vector3 c in DarkColourGrid())
        {
            Vector3 o = MixTowardLuma(c, PostSettings.DefaultShadowNeutraliseStrength);
            if (BitConverter.SingleToInt32Bits(Max(c)) != BitConverter.SingleToInt32Bits(Max(o)))
            {
                moved = true;
                break;
            }
        }

        Assert.True(moved, "a mix toward luma left the max channel alone; the exactness test proves nothing");
    }

    [Fact]
    public void NeutraliseShadows_SaturationFallsMonotonicallyWithStrength()
    {
        var c = new Vector3(0.01f, 0.02f, 0.05f);
        float previous = Saturation(c) + 1e-6f;
        for (int i = 0; i <= 20; i++)
        {
            float sat = Saturation(PostSettings.NeutraliseShadows(c, i / 20f));
            Assert.True(sat <= previous + 1e-6f, $"saturation rose at strength {i / 20f}");
            previous = sat;
        }

        Assert.True(previous < 1e-3f, "full strength should leave a near-neutral pixel");
    }

    [Fact]
    public void NeutraliseShadows_PureBlackStaysBlack()
    {
        Assert.Equal(Vector3.Zero, PostSettings.NeutraliseShadows(Vector3.Zero, 1f));
    }

    [Theory]
    [InlineData(PostSettings.ShadowNeutraliseEdge, 0.01f, 0.03f)]
    [InlineData(0.1f, 0.2f, 0.1f)]
    [InlineData(0.5f, 0.1f, 0.3f)]
    public void NeutraliseShadows_AtOrAboveTheEdge_ReturnsThePixelUnchanged(float r, float g, float b)
    {
        var c = new Vector3(r, g, b);
        Assert.Equal(c, PostSettings.NeutraliseShadows(c, 1f));
    }

    [Fact]
    public void NeutraliseShadows_IsContinuousAtTheEdge_SoThereIsNoRing()
    {
        var chroma = new Vector3(0.2f, 0.5f, 1f);
        float edge = PostSettings.ShadowNeutraliseEdge;
        Vector3 inside = PostSettings.NeutraliseShadows(chroma * (edge - 1e-5f), 1f);
        Vector3 outside = PostSettings.NeutraliseShadows(chroma * edge, 1f);
        Assert.True((inside - (chroma * edge)).Length < 1e-3f, $"a step at the edge: {inside} vs {outside}");
    }

    [Fact]
    public void TheShaderEdge_MatchesPostSettings_AndTheResolveCallsTheFunction()
    {
        string shaders = Path.Combine(RepoRoot.Find(), "dream", "shaders");
        string tone = File.ReadAllText(Path.Combine(shaders, "postTone.glsl"));
        Match match = Regex.Match(tone, @"const\s+float\s+DreamShadowNeutraliseEdge\s*=\s*([0-9.]+)\s*;");
        Assert.True(match.Success, "postTone.glsl no longer declares DreamShadowNeutraliseEdge");
        Assert.Equal(PostSettings.ShadowNeutraliseEdge, float.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), 5);

        string resolve = File.ReadAllText(Path.Combine(shaders, PostChain.ResolveShaderFile));
        Assert.Contains(
            "dreamToneNeutraliseShadows(dreamToneRolloff(scene + bloom), uShadowNeutralise)",
            resolve,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, PostSettings.DefaultShadowNeutraliseStrength)]
    [InlineData("nonsense", PostSettings.DefaultShadowNeutraliseStrength)]
    [InlineData("0", 0f)]
    [InlineData("0.5", 0.5f)]
    [InlineData("-3", 0f)]
    [InlineData("99", 1f)]
    public void TheShadowNeutraliseStrengthIsParsedAndClamped(string? value, float expected)
    {
        Assert.Equal(expected, PostSettings.ResolveShadowNeutraliseStrength(value), 5);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData(" 0 ", false)]
    public void TheChainIsOnUnlessItIsTurnedOff(string? value, bool expected)
    {
        Assert.Equal(expected, PostSettings.ResolveEnabled(value));
    }

    [Theory]
    [InlineData(null, PostSettings.DefaultBloomStrength)]
    [InlineData("nonsense", PostSettings.DefaultBloomStrength)]
    [InlineData("0", 0f)]
    [InlineData("0.5", 0.5f)]
    [InlineData("-3", 0f)]
    [InlineData("99", 2f)]
    public void TheBloomStrengthIsParsedAndClamped(string? value, float expected)
    {
        Assert.Equal(expected, PostSettings.ResolveBloomStrength(value), 5);
    }

    [Fact]
    public void TheBloomSideNeverFallsUnderTheBlursReach()
    {
        Assert.Equal(360, PostChain.BloomSide(1440));
        Assert.Equal(256, PostChain.BloomSide(1024));
        Assert.Equal(PostSettings.MinimumBloomSide, PostChain.BloomSide(1));
    }
}
