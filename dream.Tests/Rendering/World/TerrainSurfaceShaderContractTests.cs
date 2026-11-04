using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Dream.Tests.Rendering.World;

// GL is unavailable in tests, so the terrain program (cubeFragment.glsl under DREAM_TERRAIN) is
// checked through its source.
public class TerrainSurfaceShaderContractTests
{
    [Fact]
    public void The_Terrain_Block_Is_Guarded_Against_The_Mask_Variant()
    {
        string shader = StripComments(ReadShader());

        Assert.Contains("#ifdef DREAM_TERRAIN", shader);
        Assert.Matches(@"#ifdef WORLD_ALPHA_MASK\s*#error[^\n]*\n\s*#endif", shader);
        Assert.Contains("uniform float uTerrainVariation;", shader);
    }

    [Fact]
    public void The_Albedo_Samples_With_Explicit_Gradients()
    {
        string shader = StripComments(ReadShader());

        Assert.Contains("textureGrad(material.baseColorTexture", shader);
        // The stochastic sampler never uses the implicit-derivative texture().
        string variant = Body(shader, "vec4 dreamTerrainVariant(");
        Assert.DoesNotContain("texture(", variant);
    }

    [Fact]
    public void Derivatives_Are_Taken_Before_Any_Branch_In_Main()
    {
        string shader = StripComments(ReadShader());
        string main = Body(shader, "void main()");

        int dFdx = main.IndexOf("dFdx(texCoord0)", StringComparison.Ordinal);
        int dFdy = main.IndexOf("dFdy(texCoord0)", StringComparison.Ordinal);
        int firstBranch = main.IndexOf("if (", StringComparison.Ordinal);
        Assert.True(dFdx >= 0 && dFdy >= 0);
        Assert.True(dFdx < firstBranch && dFdy < firstBranch);
    }

    [Fact]
    public void Hashes_Take_Integer_Ids_And_Lattice_Corners_Never_Interpolated_Uv()
    {
        string shader = StripComments(ReadShader());

        Assert.DoesNotMatch(@"dreamTerrain(Hash2|Lattice)\([^;]*texCoord0", shader);
        Assert.DoesNotMatch(@"dreamValueNoise\([^;]*(texCoord0|\buv\b)", shader);
        // The noise is keyed by world position only.
        Assert.Contains("dreamTerrainPlane(worldPos, n)", shader);
    }

    [Fact]
    public void Main_Writes_Alpha_One_And_Keeps_The_Plain_Sample_For_Other_Programs()
    {
        string shader = StripComments(ReadShader());
        string main = Body(shader, "void main()");

        Assert.Matches(@"#ifdef DREAM_TERRAIN\s*baseColor\.a = 1\.0;\s*#endif", main);
        Assert.Matches(@"#ifndef DREAM_TERRAIN\s*baseColor \*= texture\(material\.baseColorTexture, texCoord0\);\s*#endif", main);
        Assert.Contains("uTerrainVariation > 0.5", main);
    }

    private static string Body(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        int open = source.IndexOf('{', start);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[start..(i + 1)];
        }

        throw new InvalidOperationException("Unbalanced braces.");
    }

    private static string StripComments(string source)
    {
        string withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(withoutBlocks, @"//[^\n]*", string.Empty);
    }

    private static string ReadShader() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "dream", "shaders", "cubeFragment.glsl"));

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
