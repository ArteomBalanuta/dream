using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Dream.Rendering.Core;
using Dream.Rendering.Mist;
using Dream.Tests.Utilities;
using Xunit;

namespace Dream.Tests.Rendering.Mist;

// GL is unavailable in tests, so the boundary mist is checked through its sources: the GLSL block
// against its CPU mirror, the constants against BoundaryMistField, and the way each world shader
// pulls the mist in.
public class BoundaryMistShaderContractTests
{
    private const string Include = "boundaryMist.glsl";

    [Fact]
    public void The_Glsl_Block_Mirrors_The_Cpu_Frame()
    {
        string shader = StripComments(ReadShader(Include));

        Assert.Matches(@"layout\(std140\) uniform BoundaryMist \{\s*vec4 uMistBounds;\s*vec4 uMistParams;\s*vec4 uMistEye;\s*\};", shader);
        Assert.Equal("BoundaryMist", BoundaryMistFrame.BlockName);
        Assert.Equal(3 * 16, BoundaryMistFrame.SizeInBytes);
        Assert.Equal(2, BoundaryMistFrame.BindingPoint);

        // The binding point is set after link, in Shader, like the other two blocks.
        string linker = ReadSource("dream/Rendering/Core/Shader.cs");
        Assert.Matches(@"FrameLightingBuffer\.BindProgramBlock\(program\);\s*LightListBuffer\.BindProgramBlock\(program\);\s*BoundaryMistBuffer\.BindProgramBlock\(program\);", linker);
    }

    [Fact]
    public void The_Glsl_Constants_Match_The_Cpu_Curve()
    {
        string shader = StripComments(ReadShader(Include));

        Assert.Equal(BoundaryMistField.NoiseAmp, Constant(shader, "MistNoiseAmp"));
        Assert.Equal(BoundaryMistField.NoiseFadeDistance, Constant(shader, "MistNoiseFade"));
        Assert.Equal(BoundaryMistField.Gamma, Constant(shader, "MistGamma"));
        // The ramp start is not a constant: it is uMistParams.w, uploaded from the CPU constant.
        Assert.Contains("uMistParams.w", shader);
        Assert.Equal(BoundaryMistField.RampStart, BoundaryMistFrame.ForWorld(new BoundaryMistInput(10f, 10f, 0f, true), OpenTK.Mathematics.Vector3.Zero).Params.W);
    }

    [Fact]
    public void The_Include_Stands_Alone_And_Leaves_The_Pinned_Fog_Text_Alone()
    {
        string shader = StripComments(ReadShader(Include));

        Assert.DoesNotContain("#include", shader);
        Assert.DoesNotContain("#version", shader);
        // lighting.glsl pins exactly one exp2( and one distance( (FogTransmittanceContractTests).
        Assert.DoesNotContain("exp2(", shader);
        Assert.DoesNotContain("distance(", shader);
        Assert.DoesNotContain("discard", shader);
        // Hash-based noise: no texture and no sin().
        Assert.DoesNotMatch(@"\bsampler\w*\b", shader);
        Assert.DoesNotMatch(@"\btexture\s*\(", shader);
        Assert.DoesNotMatch(@"\bsin\s*\(", shader);
        // The smoke is lit as an up-facing surface from the frame lighting, with no point lights.
        Assert.Contains("dreamAmbient(up) + 0.6 * dreamDirect(up)", shader);
        Assert.DoesNotContain("dreamPointLights", shader);
        // The noise runs only where the opacity is above zero.
        Assert.Contains("return b > 0.0 ? mix(c, dreamBoundarySmoke(p), b) : c;", shader);
        // It reads no depth.
        Assert.DoesNotContain("gl_FragCoord", shader);
    }

    [Theory]
    [InlineData("cubeFragment.glsl")]
    [InlineData("enemyFragment.glsl")]
    public void World_Fragment_Shaders_Mix_The_Fogged_Colour_Toward_The_Smoke_Last(string fileName)
    {
        string source = ReadShader(fileName);
        string code = StripComments(source);

        AssertIncludedAfterLighting(code);
        Assert.Contains(
            "FragColor = vec4(dreamApplyBoundaryMist(dreamApplyFog(dreamExpose(lit) + emissive, fragPos), fragPos), baseColor.a);",
            code);
        Assert.Single(Regex.Matches(code, @"dreamApplyBoundaryMist\("));

        // The resolved source declares the three blocks, FrameLighting first.
        string resolved = Shader.ResolveIncludes(source, ShaderDirectory());
        Assert.True(
            resolved.IndexOf("uniform FrameLighting", StringComparison.Ordinal)
            < resolved.IndexOf("uniform BoundaryMist", StringComparison.Ordinal));
    }

    [Fact]
    public void The_Fx_Fragment_Shader_Fades_Out_Where_The_Mist_Is()
    {
        string code = StripComments(ReadShader("fxFragment.glsl"));

        AssertIncludedAfterLighting(code);
        Assert.Contains(
            "FragColor = texture(uAtlas, vUv) * vColor * dreamFogTransmittance(vWorldPos) * (1.0 - dreamBoundaryOpacity(vWorldPos));",
            code);
    }

    [Fact]
    public void The_Frame_Uploads_The_Block_With_The_Other_Two_And_Disposes_It()
    {
        string source = ReadSource("dream/Rendering/Core/Render.Lighting.cs");

        Assert.Matches(@"_lightListBuffer\?\.Upload\(in _selectedLights, in view\);\s*_boundaryMistBuffer\?\.Upload\(BoundaryMist, camera\.Position\);", source);
        Assert.Matches(@"_lightListBuffer\?\.BindWorld\(\);\s*_boundaryMistBuffer\?\.BindWorld\(\);", source);
        Assert.Matches(@"_lightListBuffer\?\.BindViewModel\(\);\s*_boundaryMistBuffer\?\.BindViewModel\(\);", source);
        Assert.Contains("_boundaryMistBuffer?.Dispose();", source);
    }

    private static void AssertIncludedAfterLighting(string code)
    {
        int lighting = code.IndexOf("#include \"lighting.glsl\"", StringComparison.Ordinal);
        int mist = code.IndexOf("#include \"" + Include + "\"", StringComparison.Ordinal);
        Assert.True(lighting >= 0, "lighting.glsl is included");
        Assert.True(mist > lighting, "boundaryMist.glsl is included after lighting.glsl");
        Assert.Single(Regex.Matches(code, @"#include ""boundaryMist\.glsl"""));
    }

    private static float Constant(string shader, string name)
    {
        Match match = Regex.Match(shader, @"const float " + name + @"\s*=\s*([0-9.]+)\s*;");
        Assert.True(match.Success, name);
        return float.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static string StripComments(string source)
    {
        string withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(withoutBlocks, @"//[^\n]*", string.Empty);
    }

    private static string ShaderDirectory() => Path.Combine(RepoRoot(), "dream", "shaders");

    private static string ReadShader(string fileName) => File.ReadAllText(Path.Combine(ShaderDirectory(), fileName));

    private static string ReadSource(string relative) =>
        PartialClassSource.Read(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

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
