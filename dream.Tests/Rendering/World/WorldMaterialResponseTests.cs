using System.IO;
using System.Text.RegularExpressions;
using Dream.Rendering.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.World;

// GL is unavailable in tests, so the world material response (cubeFragment.glsl, off by default,
// reusing the uEquipSurface / uEquipEye ABI of enemyFragment.glsl) is pinned through its sources.
public class WorldMaterialResponseTests
{
    // The only terrain families that opt in this wave.
    private static readonly GeometryMaterialId[] OptedIn = [GeometryMaterialId.Stone, GeometryMaterialId.Rock];

    [Fact]
    public void World_Specular_Strength_Equals_The_Equipment_Specular_Strength()
    {
        float world = ReadConst(ReadShader("cubeFragment.glsl"), "WorldSpecularStrength");
        float equipment = ReadConst(ReadShader("enemyFragment.glsl"), "EquipSpecularStrength");
        Assert.Equal(equipment, world);
    }

    [Fact]
    public void The_Response_Is_Gated_On_The_Uniform_And_Never_On_The_Gltf_Factors()
    {
        string shader = StripComments(ReadShader("cubeFragment.glsl"));
        Assert.Contains("uniform vec4 uEquipSurface;", shader);
        Assert.Contains("uniform vec3 uEquipEye;", shader);
        Assert.Contains("if (uEquipSurface.y > 0.0)", shader);
        // The terrain pass uploads roughness 1.0 and props 0.5: reading them would switch the world on.
        // (The Material struct may declare the fields; no expression may read them.)
        Assert.DoesNotContain("material.metallicFactor", shader);
        Assert.DoesNotContain("material.roughnessFactor", shader);
    }

    [Fact]
    public void The_Response_Calls_The_Shared_Surface_Functions_After_Clouds()
    {
        string raw = ReadShader("cubeFragment.glsl");
        int clouds = raw.IndexOf("#include \"clouds.glsl\"", StringComparison.Ordinal);
        int surface = raw.IndexOf("#include \"surface.glsl\"", StringComparison.Ordinal);
        Assert.True(clouds >= 0 && surface > clouds);

        string shader = StripComments(raw);
        foreach (string call in new[] { "dreamSurfaceShininess(", "dreamSurfaceF0(", "dreamEquipLobe(", "dreamPointSpecular(", "dreamSurfaceRim(" })
        {
            Assert.Contains(call, shader);
        }

        // Diffuse-surface only: no grain, detail, pattern or brush.
        Assert.DoesNotContain("uEquipDetail", shader);
        Assert.DoesNotContain("dreamEquipPattern", shader);
        Assert.DoesNotContain("dreamEquipGrain", shader);
        Assert.DoesNotContain("dreamEquipStreak", shader);
    }

    [Fact]
    public void Every_Material_Is_Off_Except_The_Opted_In_Families()
    {
        foreach (GeometryMaterialId id in Enum.GetValues<GeometryMaterialId>())
        {
            foreach (StaticTerrainMaterialRole role in Enum.GetValues<StaticTerrainMaterialRole>())
            {
                Vector4 surface = StaticTerrainMaterialCatalog.Resolve(id, role).Surface;
                if (Array.IndexOf(OptedIn, id) >= 0)
                {
                    Assert.True(surface.Y > 0f, $"{id}/{role} should opt in");
                    Assert.InRange(surface.X, 0f, 0.1f);
                    Assert.InRange(surface.Y, 0.45f, 0.55f);
                }
                else
                {
                    Assert.Equal(Vector4.Zero, surface);
                }
            }
        }
    }

    private static float ReadConst(string source, string name)
    {
        Match m = Regex.Match(StripComments(source), @"const\s+float\s+" + name + @"\s*=\s*([0-9.]+)\s*;");
        Assert.True(m.Success, $"{name} not found");
        return float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string StripComments(string source)
    {
        string withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(withoutBlocks, @"//[^\n]*", string.Empty);
    }

    private static string ReadShader(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "dream", "shaders", name));

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
