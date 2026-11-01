using System.IO;
using System.Text.RegularExpressions;
using Dream.Rendering.Core;
using Xunit;

namespace Dream.Tests.Rendering.Core;

// Emissive textures (LDR effects wave, WS2): both world fragment shaders sample the emissive
// texture only behind a flag, add no discard, terrain resets the flag the shared cube program may
// have inherited from AnimatedModel, and the frame clock wraps. No shipped glb has an emissive
// texture yet, so this is verified by contract only.
public class EmissiveMaterialContractTests
{
    [Theory]
    [InlineData("cubeFragment.glsl")]
    [InlineData("enemyFragment.glsl")]
    public void Emissive_Texture_Is_Sampled_Only_Behind_Its_Flag(string fileName)
    {
        string shader = StripComments(File.ReadAllText(Path.Combine(RepoRoot(), "dream", "shaders", fileName)));

        Assert.Matches(@"struct Material \{[^}]*\bbool hasEmissiveTexture;[^}]*\bsampler2D emissiveTexture;[^}]*\bvec2 emissiveUvScroll;[^}]*\};", shader);
        Assert.Contains("uniform float uTime;", shader);
        Assert.Matches(
            @"vec3 emissive = material\.emissiveFactor;\s*if \(material\.hasEmissiveTexture\) \{\s*emissive \*= texture\(material\.emissiveTexture, texCoord0 \+ material\.emissiveUvScroll \* uTime\)\.rgb;\s*\}\s*FragColor = vec4\(dreamApplyFog\(dreamExpose\(lit\) \+ emissive, fragPos\), baseColor\.a\);",
            shader);
        Assert.Single(Regex.Matches(shader, @"material\.emissiveTexture"));
        // Early-z: the only discard stays the MASK variant's cutoff one.
        Assert.Single(Regex.Matches(shader, @"\bdiscard\b"));
    }

    [Fact]
    public void Cube_Fragment_Shader_Keeps_Its_Bom()
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(RepoRoot(), "dream", "shaders", "cubeFragment.glsl"));

        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
    }

    [Fact]
    public void Shader_Setters_Use_Link_Time_Locations_And_Reset_On_Dispose()
    {
        string shader = File.ReadAllText(Path.Combine(RepoRoot(), "dream", "Rendering", "Core", "Shader.cs"));

        Assert.Equal("material.hasEmissiveTexture", Shader.HasEmissiveTextureUniformName);
        Assert.Equal("material.emissiveUvScroll", Shader.EmissiveUvScrollUniformName);
        Assert.Equal("uTime", Shader.TimeUniformName);
        Assert.Contains("_hasEmissiveTextureLocation = GetUniformLocation(HasEmissiveTextureUniformName);", shader);
        Assert.Contains("_emissiveUvScrollLocation = GetUniformLocation(EmissiveUvScrollUniformName);", shader);
        Assert.Contains("_timeLocation = GetUniformLocation(TimeUniformName);", shader);
        string dispose = MethodBody(shader, "public void Dispose(");
        Assert.Contains("_hasEmissiveTextureLocation = -1;", dispose);
        Assert.Contains("_emissiveUvScrollLocation = -1;", dispose);
        Assert.Contains("_timeLocation = -1;", dispose);
        Assert.Contains("_uploadedHasEmissiveTexture = -1;", shader);
        Assert.Contains("_uploadedEmissiveUvScroll = new(float.NaN);", shader);
        Assert.Contains("_uploadedTime = float.NaN;", shader);
    }

    [Fact]
    public void Animated_Model_Uploads_The_Flag_Per_Material_And_The_Time_Only_When_Scrolling()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "dream", "Rendering", "Models", "AnimatedModel.cs"));
        string body = MethodBody(source, "private void SetMaterialProperties(");

        Assert.Contains("shader.SetHasEmissiveTexture(material.HasEmissiveTexture);", body);
        Assert.Contains("shader.SetEmissiveUvScroll(material.EmissiveUvScroll);", body);
        Assert.DoesNotContain("\"material.hasEmissiveTexture\"", source);
        Assert.Matches(
            @"if \(material\.EmissiveUvScroll != Vector2\.Zero\)\s*\{\s*shader\.SetTime\(RenderFrameClock\.Seconds\);\s*\}",
            body);
        Assert.Contains("EmissiveUvScroll = source.EmissiveUvScroll,", source);
    }

    [Fact]
    public void Terrain_Resets_The_Flag_In_Both_Paths()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "dream", "Rendering", "Core", "Render.Terrain.cs"));

        Assert.Contains("shader.SetHasEmissiveTexture(false);", MethodBody(source, "public void BeginStaticTerrainPass("));
        Assert.Equal(2, Regex.Matches(source, @"SetHasEmissiveTexture\(false\);").Count);
        // The fallback cube path (not the batched one) resets it next to the emissive factor.
        Assert.Matches(@"SetEmissiveFactor\(material\.Emissive\);\s*shader\.SetHasEmissiveTexture\(false\);", source);
    }

    [Fact]
    public void Update_Advances_The_Frame_Clock_From_The_Presentation_Clock()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "dream", "Client", "Scenes", "PlayScene.Update.cs"));

        Assert.Matches(
            @"_clientPresentationTimeSeconds \+= args\.Time;\s*RenderFrameClock\.Advance\(_clientPresentationTimeSeconds\);",
            source);
    }

    [Fact]
    public void Frame_Clock_Wraps_To_Keep_Float_Precision()
    {
        try
        {
            RenderFrameClock.Advance(12.5);
            Assert.Equal(12.5f, RenderFrameClock.Seconds);

            RenderFrameClock.Advance(RenderFrameClock.WrapSeconds + 3d);
            Assert.Equal(3f, RenderFrameClock.Seconds);

            RenderFrameClock.Advance(RenderFrameClock.WrapSeconds * 1000d + 0.25);
            Assert.Equal(0.25f, RenderFrameClock.Seconds);
        }
        finally
        {
            RenderFrameClock.Advance(0d);
        }
    }

    private static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} not found.");
        int open = source.IndexOf('{', start);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}' && --depth == 0)
            {
                return source.Substring(open, i - open + 1);
            }
        }

        throw new InvalidOperationException($"{signature} body not closed.");
    }

    private static string StripComments(string source) =>
        Regex.Replace(Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline), @"//[^\n]*", string.Empty);

    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "dream.sln")) &&
            !Directory.Exists(Path.Combine(directory.FullName, "dream", "shaders")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
