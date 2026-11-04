using System.IO;
using System.Text.RegularExpressions;
using Dream.Rendering.Core;
using Xunit;

namespace Dream.Tests.Rendering.World;

// Pins the pairing between the uniform names the C# side sets for the terrain program and the ones
// cubeFragment.glsl declares inside its DREAM_TERRAIN block, and the texture units they sample.
public class TerrainUniformPairingContractTests
{
    private static readonly string[] CsFiles =
    [
        Path.Combine("Rendering", "Core", "Render.Terrain.cs"),
        Path.Combine("Rendering", "Core", "TerrainMaterialMapTexture.cs"),
        Path.Combine("Rendering", "Core", "StaticTerrainTileArray.cs"),
        Path.Combine("Rendering", "Core", "Render.Shaders.cs")
    ];

    private static readonly string[] Expected =
    [
        "uTerrainTiles", "uTerrainMaterialMap", "uTerrainMapOrigin", "uTerrainMapSize", "uTerrainLayer",
        "uTerrainBlend", "uTerrainBlendBand", "uTerrainBlendDepth", "uTerrainRelief", "uTerrainVariation"
    ];

    [Fact]
    public void Every_Uniform_Set_From_CSharp_Is_Declared_In_The_Terrain_Block_And_Vice_Versa()
    {
        HashSet<string> fromCs = [];
        foreach (string file in CsFiles)
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(Path.Combine(RepoRoot(), "dream", file)), "\"(uTerrain\\w+)\""))
            {
                fromCs.Add(m.Groups[1].Value);
            }
        }

        string block = TerrainBlock(StripComments(ReadShader()));
        HashSet<string> declared = [];
        foreach (Match m in Regex.Matches(block, @"^\s*uniform\s+\w+\s+(uTerrain\w+)\s*;", RegexOptions.Multiline))
        {
            declared.Add(m.Groups[1].Value);
        }

        Assert.Equal(Expected.OrderBy(s => s), fromCs.OrderBy(s => s));
        Assert.Equal(Expected.OrderBy(s => s), declared.OrderBy(s => s));
    }

    [Fact]
    public void No_Terrain_Uniform_Is_Declared_Outside_The_Terrain_Block()
    {
        string shader = StripComments(ReadShader());
        string block = TerrainBlock(shader);
        string outside = shader.Replace(block, string.Empty, StringComparison.Ordinal);

        Assert.DoesNotMatch(@"uniform\s+\w+\s+uTerrain", outside);
    }

    [Fact]
    public void Terrain_Texture_Units_Are_Distinct_From_Each_Other_And_From_The_War_Mist_Unit()
    {
        Assert.Equal(2, Render.TerrainTilesUnit);
        Assert.Equal(3, Render.TerrainMaterialMapUnit);
        Assert.Equal(Render.TerrainMaterialMapUnit, TerrainMaterialMapTexture.TextureUnitIndex);
        Assert.NotEqual(Render.TerrainTilesUnit, Render.TerrainMaterialMapUnit);

        string source = File.ReadAllText(Path.Combine(RepoRoot(), "dream", "Rendering", "Core", "StaticTerrainTileArray.cs"));
        Assert.Contains("TextureUnit.Texture2", source);
        Assert.DoesNotContain("TextureUnit.Texture1", source);
        Assert.DoesNotContain("TextureUnit.Texture3", source);
    }

    [Fact]
    public void Uniform_Name_Constants_Match_The_Shader()
    {
        Assert.Equal("uTerrainTiles", Render.TerrainTilesUniform);
        Assert.Equal("uTerrainMaterialMap", Render.TerrainMaterialMapUniform);
        Assert.Equal("uTerrainMapOrigin", TerrainMaterialMapTexture.OriginUniform);
        Assert.Equal("uTerrainMapSize", TerrainMaterialMapTexture.SizeUniform);
    }

    // The text from "#ifdef DREAM_TERRAIN" to its matching #endif.
    private static string TerrainBlock(string shader)
    {
        int start = shader.IndexOf("#ifdef DREAM_TERRAIN", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int depth = 0;
        foreach (Match m in Regex.Matches(shader[start..], @"^\s*#\s*(if\w*|endif)\b", RegexOptions.Multiline))
        {
            depth += m.Groups[1].Value == "endif" ? -1 : 1;
            if (depth == 0)
            {
                return shader.Substring(start, m.Index + m.Length);
            }
        }

        throw new InvalidOperationException("Unbalanced DREAM_TERRAIN block.");
    }

    private static string StripComments(string source) =>
        Regex.Replace(Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline), @"//[^\n]*", string.Empty);

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
