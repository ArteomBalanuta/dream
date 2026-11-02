using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Dream.Tests.Rendering.Lighting;

// The effects pass needs the fog's transmittance on its own (it scales premultiplied colour and
// alpha instead of mixing towards the fog colour), so lighting.glsl exposes it as a helper and
// dreamApplyFog is defined over it. The world's fogged result must not change.
public class FogTransmittanceContractTests
{
    private static string Lighting() =>
        File.ReadAllText(Path.Combine(ShaderDirectory(), "lighting.glsl"));

    [Fact]
    public void Transmittance_Helper_Holds_The_Exp2_Squared_Fog_Maths()
    {
        string body = Body(Lighting(), "float dreamFogTransmittance(vec3 fragPos)");

        Assert.Matches(@"float d = max\(distance\(fragPos, uCameraPosition\.xyz\) - uCameraPosition\.w, 0\.0\);", body);
        Assert.Matches(@"float k = uFogColor\.w \* d;", body);
        Assert.Matches(@"return clamp\(exp2\(-k \* k\), 0\.0, 1\.0\);", body);
    }

    [Fact]
    public void Apply_Fog_Is_The_Mix_Over_The_Helper()
    {
        string source = Lighting();

        Assert.Matches(
            @"vec3 dreamApplyFog\(vec3 c, vec3 fragPos\) \{ return mix\(uFogColor\.rgb, c, dreamFogTransmittance\(fragPos\)\); \}",
            source);
        Assert.Single(Regex.Matches(source, @"exp2\("));
        Assert.Single(Regex.Matches(source, @"\bdistance\("));
    }

    [Fact]
    public void The_Helper_Is_Declared_Before_Its_Only_Caller()
    {
        string source = Lighting();

        Assert.True(source.IndexOf("float dreamFogTransmittance(", StringComparison.Ordinal)
            < source.IndexOf("vec3 dreamApplyFog(", StringComparison.Ordinal));
    }

    // The CPU mirror of the helper, as the shader computes it.
    private static float Transmittance(float distance, float cameraRadius, float density)
    {
        float d = MathF.Max(distance - cameraRadius, 0f);
        float k = density * d;
        return Math.Clamp(MathF.Pow(2f, -k * k), 0f, 1f);
    }

    [Fact]
    public void Transmittance_Is_One_Inside_The_Fog_Start_And_Falls_Monotonically_To_Zero()
    {
        Assert.Equal(1f, Transmittance(3f, 5f, 0.02f));
        float previous = 1f;
        for (float distance = 5f; distance < 400f; distance += 5f)
        {
            float value = Transmittance(distance, 5f, 0.02f);
            Assert.InRange(value, 0f, previous);
            previous = value;
        }

        Assert.True(previous < 0.001f);
    }

    [Fact]
    public void A_Mix_Over_The_Transmittance_Is_The_Old_Fog_Result()
    {
        float[] distances = [0f, 4f, 12f, 40f, 160f];
        foreach (float distance in distances)
        {
            float t = Transmittance(distance, 2f, 0.03f);
            float colour = 0.8f;
            float fog = 0.3f;
            float viaHelper = fog + (colour - fog) * t;
            float k = 0.03f * MathF.Max(distance - 2f, 0f);
            float legacy = fog + (colour - fog) * Math.Clamp(MathF.Pow(2f, -k * k), 0f, 1f);
            Assert.Equal(legacy, viaHelper, 6);
        }
    }

    private static string Body(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        int open = source.IndexOf('{', start);
        int close = source.IndexOf('}', open);
        return source[open..(close + 1)];
    }

    private static string ShaderDirectory() => Path.Combine(RepoRoot(), "dream", "shaders");

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "dream", "dream.csproj")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
