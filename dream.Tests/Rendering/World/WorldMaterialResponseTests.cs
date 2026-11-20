using System.IO;
using System.Text.RegularExpressions;
using Dream.Rendering.Actors;
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
        Assert.True(GatesOnSurfaceRoughness(shader), "the response is no longer gated on uEquipSurface.y > 0.0");
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
                EquipmentSurface surface = StaticTerrainMaterialCatalog.Resolve(id, role).Surface;
                if (Array.IndexOf(OptedIn, id) >= 0)
                {
                    Assert.True(surface.Roughness > 0f, $"{id}/{role} should opt in");
                    Assert.InRange(surface.Metallic, 0f, 0.1f);
                    Assert.InRange(surface.Roughness, 0.45f, 0.55f);
                }
                else
                {
                    Assert.Equal(EquipmentSurface.None, surface);
                }
            }
        }
    }

    // The gate, as a match on its meaning rather than its spelling: `if (uEquipSurface.y > 0.0)`, with
    // any spacing, an optional f suffix, or a literal 0 - but never `>= 0`, `!= 0` or `> 0.5`, which
    // would switch the world on (or off) wrongly.
    private static readonly Regex SurfaceGate = new(
        @"if\s*\(\s*uEquipSurface\s*\.\s*y\s*>\s*0(?:\.0*)?f?\s*\)");

    private static bool GatesOnSurfaceRoughness(string shaderWithoutComments) =>
        SurfaceGate.IsMatch(shaderWithoutComments);

    // The synthetic negative: spellings that look like the gate but are not it must NOT match, and
    // equivalent spellings must, or the scan above proves nothing in either direction.
    [Theory]
    [InlineData("if (uEquipSurface.y > 0.0f) {", true)]
    [InlineData("if( uEquipSurface.y>0.0 ){", true)]
    [InlineData("if (uEquipSurface.y > 0) {", true)]
    [InlineData("if (uEquipSurface.y >= 0.0) {", false)]
    [InlineData("if (uEquipSurface.y != 0.0) {", false)]
    [InlineData("if (uEquipSurface.y > 0.5) {", false)]
    [InlineData("if (uEquipSurface.x > 0.0) {", false)]
    [InlineData("if (material.roughnessFactor > 0.0) {", false)]
    [InlineData("", false)]
    public void TheGateScan_AcceptsTheSpellingsOfTheGate_AndRejectsLookAlikes(string line, bool expected)
    {
        Assert.Equal(expected, GatesOnSurfaceRoughness(line));
    }

    // Every path that binds the terrain program or draws with it must write the surface, through
    // BindTerrainSurface (the single writer). Nothing else resets it: AnimatedModel.BindProgram is
    // never called for this program. So a new draw path that forgets is caught here, not as a
    // sheen that leaks onto a prop.
    private static readonly Regex DrawOrBind = new(@"\.Use\(\)|\.Draw\(|\.DrawInstanced\(|GL\.Draw");

    internal static IEnumerable<string> TerrainPathsMissingTheSurface(string source)
    {
        foreach (Match signature in Regex.Matches(source, @"^    (?:public|private|internal)\s[^;=\n{]*\([^)]*\)\s*\{", RegexOptions.Multiline))
        {
            string body = BraceBody(source, signature.Index + signature.Length - 1);
            if (!DrawOrBind.IsMatch(body))
            {
                continue;
            }

            string name = Regex.Match(signature.Value, @"(\w+)\s*\(").Groups[1].Value;
            bool reachesTheSurface = body.Contains("BindTerrainSurface(", StringComparison.Ordinal)
                || body.Contains("BindTerrainMaterial(", StringComparison.Ordinal)
                || body.Contains("BindStaticTerrainMaterial(", StringComparison.Ordinal);
            if (!reachesTheSurface)
            {
                yield return name;
            }
        }
    }

    private static string BraceBody(string source, int open)
    {
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}' && --depth == 0)
            {
                return source[open..(i + 1)];
            }
        }

        throw new InvalidOperationException("Unbalanced braces.");
    }

    private static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        return BraceBody(source, source.IndexOf('{', start));
    }

    [Fact]
    public void Every_Terrain_Bind_Or_Draw_Path_Writes_The_Surface_Through_One_Helper()
    {
        string source = StripComments(File.ReadAllText(Path.Combine(RepoRoot(), "dream", "Rendering", "Core", "Render.Terrain.cs")));

        Assert.Empty(TerrainPathsMissingTheSurface(source));

        // The paths that bind the program write the surface themselves, not by hearsay...
        foreach (string binder in new[] { "public void BeginStaticTerrainPass(", "public void BindStaticTerrainMaterial(", "private void BindTerrainMaterial(" })
        {
            Assert.Contains("BindTerrainSurface(", MethodBody(source, binder));
        }

        // ...and the helper is the only writer, so the packing and the reset cannot fork.
        Assert.Equal(1, Regex.Matches(source, @"SetEquipmentSurface\(").Count);
        Assert.Contains("SetEquipmentSurface(surface.ToUniform())", MethodBody(source, "private static void BindTerrainSurface("));
    }

    [Fact]
    public void TheTerrainSurfaceGate_FailsForADrawPathThatForgetsTheSurface()
    {
        const string leaky = """
            public partial class Render
            {
                public void DrawSomethingNew(Camera camera)
                {
                    Shader shader = GetShader(TerrainShaderName);
                    shader.Use();
                    _mesh.Draw();
                }

                private void Fine()
                {
                    BindTerrainMaterial(camera, m, material, false);
                    _mesh.Draw();
                }
            }
            """;
        // Indented as in the real file: the signature scan keys on four-space members.
        string indented = string.Join('\n', leaky.Split('\n').Select(line => line.Length == 0 ? line : line));
        Assert.Equal(["DrawSomethingNew"], TerrainPathsMissingTheSurface(indented).ToArray());
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
