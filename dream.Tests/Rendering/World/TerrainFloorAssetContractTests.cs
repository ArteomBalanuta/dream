using System.IO;
using System.Linq;
using System.Text;
using Dream.Rendering.Core;
using Xunit;

namespace Dream.Tests.Rendering.World;

// Source-level and asset-level guards for the terrain floor: shader file headers (a doubled BOM once
// broke GLSL compilation), the single terrain-only program, and the forge albedo tiles that carry
// relief height in alpha.
public class TerrainFloorAssetContractTests
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    public static IEnumerable<object[]> ShaderFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "dream", "shaders"), "*.glsl")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new object[] { Path.GetFileName(path) });

    [Theory]
    [MemberData(nameof(ShaderFiles))]
    public void Shader_Has_At_Most_One_Bom_And_Standalone_Programs_Start_With_Version(string fileName)
    {
        string dir = Path.Combine(RepoRoot(), "dream", "shaders");
        byte[] bytes = File.ReadAllBytes(Path.Combine(dir, fileName));
        int offset = bytes.AsSpan().StartsWith(Bom) ? Bom.Length : 0;

        Assert.False(bytes.AsSpan(offset).StartsWith(Bom), $"{fileName} has a doubled BOM");

        // Files pulled in with #include are spliced into the middle of another shader, so a BOM
        // there would be a stray character mid-source.
        bool isInclude = Directory.EnumerateFiles(dir, "*.glsl")
            .Any(other => File.ReadAllText(other).Contains("#include \"" + fileName + "\"") && !other.EndsWith(fileName, StringComparison.Ordinal));
        if (isInclude)
        {
            Assert.Equal(0, offset);
            return;
        }

        string head = Encoding.UTF8.GetString(bytes, offset, Math.Min(32, bytes.Length - offset));
        Assert.StartsWith("#version", head);
    }

    [Fact]
    public void Only_The_Cube_Fragment_Declares_The_Terrain_Uniform_Or_Define()
    {
        foreach (string path in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "dream", "shaders"), "*.glsl"))
        {
            string text = File.ReadAllText(path);
            bool mentions = text.Contains("uTerrainVariation") || text.Contains("DREAM_TERRAIN");
            Assert.Equal(Path.GetFileName(path) == "cubeFragment.glsl", mentions);
        }
    }

    [Fact]
    public void Terrain_Program_Is_Registered_Once_Without_A_Mask_Variant()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "dream", "Rendering", "Core", "Render.Shaders.cs"));

        Assert.Equal("terrain", Render.TerrainShaderName);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(source, @"LoadTerrainShader\(TerrainShaderName"));
        int start = source.IndexOf("public void LoadTerrainShader", StringComparison.Ordinal);
        Assert.True(start >= 0);
        string body = source[start..];
        Assert.Contains("Shader.TerrainDefine", body);
        Assert.DoesNotContain("AttachAlphaMaskVariant", body);
        Assert.DoesNotContain("AlphaMaskDefine", body);
    }

    [Fact]
    public void Terrain_Uniform_Is_Only_Set_From_Terrain_Render_Code()
    {
        string coreDir = Path.Combine(RepoRoot(), "dream");
        foreach (string path in Directory.EnumerateFiles(coreDir, "*.cs", SearchOption.AllDirectories)
                     .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                              && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            if (File.ReadAllText(path).Contains("\"uTerrainVariation\""))
            {
                Assert.Equal("Render.Terrain.cs", Path.GetFileName(path));
            }
        }
    }

    [Fact]
    public void Every_Forge_Terrain_Albedo_Is_256_Rgba_With_Height_In_Alpha()
    {
        string forge = Path.Combine(RepoRoot(), "dream", "animations", "library", "forge");
        string[] names = ["dirt", "grass_bottom", "grass_path", "grass_side", "grass_top", "mud", "rock", "sand", "snow", "stone"];

        foreach (string name in names)
        {
            DecodedTexture texture = TextureDecoder.DecodeRgba(Path.Combine(forge, name, $"{name}.albedo.png"));

            Assert.True(texture.Width == 256 && texture.Height == 256, $"{name} is {texture.Width}x{texture.Height}");
            Assert.Equal(256 * 256 * 4, texture.Rgba.Length);
            bool varies = false;
            for (int i = 3; i < texture.Rgba.Length; i += 4)
            {
                if (texture.Rgba[i] != 255)
                {
                    varies = true;
                    break;
                }
            }

            Assert.True(varies, $"{name} alpha is all 255, so no height is stored");
        }
    }

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
