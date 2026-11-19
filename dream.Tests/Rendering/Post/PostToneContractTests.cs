using System.Globalization;
using System.Text.RegularExpressions;
using Dream.Rendering.Post;
using Dream.Tests.Integration;
using Xunit;

namespace Dream.Tests.Rendering.Post;

// The highlight shoulder is the one transform the post chain applies to every pixel in the game, so
// the three properties it was chosen for are pinned here rather than left to a capture to notice.
//
// 1. It is the IDENTITY below the knee. This is what makes the pass safe to add to a renderer whose
//    whole art catalogue - albedos, fog, tints, the character palette gates - was calibrated against
//    a hard clip at 1.0. Almost every pixel in a frame is under the knee and must come out of the
//    chain exactly as it went in.
// 2. Its slope at the knee is exactly 1, so there is no step and no band where the curve takes over.
// 3. It never reaches 1.0, so nothing clips however bright it gets. That is the point of the pass.
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
