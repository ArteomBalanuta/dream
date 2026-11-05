using System.Text.RegularExpressions;
using Dream.Rendering.Sky;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Sky;

// WHAT THIS IS AND IS NOT. GLSL cannot run in a test, so nothing here proves that clouds.glsl and
// CloudLayer.cs compute the same numbers. These are TRIPWIRES: they pin the exact arithmetic text of the
// GLSL lines CloudLayer mirrors, and pin CloudLayer's output at fixed points. Editing either side alone
// fails a test here, which is the cue to re-verify the other side by hand (and then update the pins).
// Whitespace is normalised, so reformatting does not trip them. The constants are already compared by
// CloudLayerTests.The_Csharp_Constants_Are_The_Glsl_Constants.
public class CloudLayerGoldenTests
{
    // GLSL line  ->  the CloudLayer.cs expression it is mirrored by.
    private static readonly (string Glsl, string Mirror)[] Lines =
    [
        ("vec3 p3 = fract(vec3(p.xyx) * 0.1031);", "Hash: x,y,z = Fract(p.X*0.1031), Fract(p.Y*0.1031), Fract(p.X*0.1031)"),
        ("p3 += dot(p3, p3.yzx + 33.33);", "Hash: d = x*(y+33.33)+y*(z+33.33)+z*(x+33.33); x,y,z += d"),
        ("return fract((p3.x + p3.y) * p3.z);", "Hash: Fract((x+y)*z)"),
        ("vec2 u = f * f * (3.0 - 2.0 * f);", "ValueNoise: ux, uy"),
        ("float b = dreamCloudHash(cell + vec2(1.0, 0.0));", "ValueNoise: b"),
        ("float c = dreamCloudHash(cell + vec2(0.0, 1.0));", "ValueNoise: c"),
        ("float d = dreamCloudHash(cell + vec2(1.0, 1.0));", "ValueNoise: d"),
        ("return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);", "ValueNoise: Mix(Mix(a,b,ux),Mix(c,d,ux),uy)"),
        ("if (uCloudParams.x <= 0.0) { return 0.0; }", "Cover: early out"),
        ("vec2 p = (planeXZ - CloudDirection * (uCloudParams.w * uFrameTime.x)) * CloudCellsPerUnit;", "Cover: p"),
        ("sum += amplitude * dreamCloudValueNoise(p);", "Cover: octave sum"),
        ("p = p * CloudLacunarity + vec2(17.0, 31.0);", "Cover: octave step"),
        ("amplitude *= CloudGain;", "Cover: amplitude"),
        ("float n = clamp((sum / CloudAmplitudeSum - 0.5) * CloudContrast + 0.5, 0.0, 1.0);", "Cover: n"),
        ("float w = clamp(uCloudParams.y, 0.0, 1.0) * CloudSoftnessScale + 0.0005;", "Cover: w"),
        ("float center = mix(1.0 + w, -w, clamp(uCloudParams.x, 0.0, 1.0));", "Cover: center"),
        ("return smoothstep(center - w, center + w, n);", "Cover: SmoothStep"),
        ("if (uCloudParams.x > 0.0 && uSunDirection.y > 0.02)", "SunMask: guard"),
        ("vec3 onPlane = fragPos + uSunDirection.xyz * ((CloudPlaneY - fragPos.y) / uSunDirection.y);", "SunMask: onPlane"),
        ("sunMask = 1.0 - uCloudParams.z * dreamCloudCover(onPlane.xz);", "SunMask: 1 - strength * cover"),
    ];

    [Fact]
    public void The_Glsl_Arithmetic_That_CloudLayer_Mirrors_Is_Unchanged()
    {
        string glsl = Normalise(File.ReadAllText(Path.Combine(ShaderDirectory(), "clouds.glsl")));
        foreach ((string line, string mirror) in Lines)
        {
            Assert.True(
                glsl.Contains(Normalise(line), StringComparison.Ordinal),
                $"clouds.glsl no longer contains `{line}`. CloudLayer.cs mirrors it as [{mirror}]; re-verify the C# by hand, then update this pin.");
        }

        Assert.Equal(1, Regex.Matches(glsl, Regex.Escape(Normalise("sum += amplitude * dreamCloudValueNoise(p);"))).Count);
    }

    // Golden values of CloudLayer.Cover (authored params). A change to CloudLayer's arithmetic moves
    // these; the GLSL lines above must then be re-checked against it. Single points (most of the sky is
    // clear at this coverage, so these are the fractional ones) plus the sum over a 64 x 64 grid at three
    // clocks, which sees every octave and the clock drift.
    [Theory]
    [InlineData(310f, 205f, 1000f, 0.560156f)]
    [InlineData(120.5f, -310.25f, 0f, 0.224910f)]
    public void CloudLayer_Cover_Matches_Its_Pinned_Points(float x, float z, float clock, float expected)
    {
        Assert.Equal(expected, CloudLayer.Cover(new Vector2(x, z), Authored, clock), 4);
    }

    [Theory]
    [InlineData(0f, 1160.755f)]
    [InlineData(45f, 1133.329f)]
    [InlineData(1800f, 1114.570f)]
    public void CloudLayer_Cover_Grid_Sum_Matches_Its_Pinned_Value(float clock, float expected)
    {
        double sum = 0.0;
        for (int i = 0; i < 64; i++)
        {
            for (int j = 0; j < 64; j++)
            {
                sum += CloudLayer.Cover(new Vector2((i * 53f) - 1600f, (j * 47f) - 1500f), Authored, clock);
            }
        }

        Assert.True(Math.Abs(sum - expected) < 0.05, $"grid sum {sum} against pinned {expected}");
    }

    private static readonly Vector4 Authored = new(0.35f, 0.35f, 0.55f, 0.9f);

    private static string Normalise(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    private static string ShaderDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "dream", "dream.csproj")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Repository root not found."), "dream", "shaders");
    }
}
