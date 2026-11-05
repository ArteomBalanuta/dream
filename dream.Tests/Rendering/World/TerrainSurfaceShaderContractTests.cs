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

        Assert.Matches(@"#ifdef DREAM_TERRAIN\s*baseColor\.a = 1\.0;", main);
        Assert.Matches(@"#ifndef DREAM_TERRAIN\s*baseColor \*= texture\(material\.baseColorTexture, texCoord0\);\s*#endif", main);
        Assert.Contains("uTerrainVariation > 0.5", main);
    }

    [Fact]
    public void The_Tile_Array_Is_Sampled_Only_With_Explicit_Gradients()
    {
        string shader = StripComments(ReadShader());

        Assert.Contains("textureGrad(uTerrainTiles, vec3(x.t, float(layer)), x.dx, x.dy)", shader);
        Assert.DoesNotMatch(@"\btexture\(\s*uTerrainTiles", shader);
        Assert.DoesNotMatch(@"\btexture\(\s*uTerrainMaterialMap", shader);
        // The material map is read with texelFetch only, after a bounds check.
        Assert.Contains("texelFetch(uTerrainMaterialMap, i, 0)", shader);
        Assert.Matches(@"any\(lessThan\(i, ivec3\(0\)\)\) \|\| any\(greaterThanEqual\(i, size\)\)", shader);
        Assert.Contains("sampler2DArray uTerrainTiles;", shader);
        Assert.Contains("usampler3D uTerrainMaterialMap;", shader);
    }

    [Fact]
    public void The_Blend_Uniforms_And_Helpers_Exist_Only_Inside_The_Terrain_Block()
    {
        string shader = StripComments(ReadShader());
        string[] declared =
        [
            "int uTerrainLayer;", "float uTerrainBlend;", "float uTerrainBlendBand;", "float uTerrainBlendDepth;",
            "float uTerrainRelief;", "vec3 uTerrainMapOrigin;", "vec3 uTerrainMapSize;",
        ];
        foreach (string declaration in declared)
        {
            Assert.Contains("uniform " + declaration, shader);
        }

        string outside = StripTerrainBlocks(shader);
        Assert.DoesNotContain("uTerrain", outside);
        Assert.DoesNotContain("dreamTerrain", outside);
        Assert.DoesNotContain("DreamTerrain", outside);
        Assert.DoesNotContain("kLayerHeightBias", outside);
        Assert.Contains("void main()", outside);
    }

    [Fact]
    public void Height_Bias_Table_Lists_The_Eight_Layers_In_Material_Order()
    {
        string shader = StripComments(ReadShader());

        // grass, dirt, stone, sand, mud, snow, rock, grass_path
        Assert.Contains(
            "const float kLayerHeightBias[8] = float[8](0.0, 0.0, 0.1, -0.1, -0.1, 0.0, 0.1, 0.0);",
            shader);
    }

    [Fact]
    public void The_Height_Derivatives_Are_Taken_In_Uniform_Control_Flow_Before_The_Bump()
    {
        string shader = StripComments(ReadShader());
        string main = Body(shader, "void main()");

        int dFdx = main.IndexOf("dFdx(terrainHeight)", StringComparison.Ordinal);
        int dFdy = main.IndexOf("dFdy(terrainHeight)", StringComparison.Ordinal);
        Assert.True(dFdx >= 0 && dFdy >= 0);
        // Directly in main's own scope: not inside any if, for or other block.
        Assert.Equal(1, Depth(main, dFdx));
        Assert.Equal(1, Depth(main, dFdy));
        // After the material branches that assign the height, before the bump consumes it.
        Assert.True(dFdx > main.IndexOf("dreamTerrainAlbedo(", StringComparison.Ordinal));
        int bump = main.IndexOf("n = dreamTerrainBump(", StringComparison.Ordinal);
        Assert.True(bump > dFdy);
        // No derivative call hides in the helpers' branches: the helpers receive the values.
        string albedo = Body(shader, "vec3 dreamTerrainAlbedo(");
        string bumpFn = Body(shader, "vec3 dreamTerrainBump(");
        Assert.DoesNotContain("dFd", albedo);
        Assert.DoesNotContain("dFd", bumpFn);
        // The position derivatives come from main's top, before any if.
        int posDx = main.IndexOf("dFdx(fragPos)", StringComparison.Ordinal);
        Assert.True(posDx >= 0 && posDx < main.IndexOf("if (", StringComparison.Ordinal));
    }

    [Fact]
    public void The_Bumped_Normal_Feeds_Every_Light()
    {
        string shader = StripComments(ReadShader());
        string main = Body(shader, "void main()");

        int normalDecl = main.IndexOf("vec3 n = normalize(normal);", StringComparison.Ordinal);
        int bump = main.IndexOf("n = dreamTerrainBump(n,", StringComparison.Ordinal);
        int lights = main.IndexOf("dreamPointLights(n, fragPos)", StringComparison.Ordinal);
        Assert.True(normalDecl >= 0 && normalDecl < bump && bump < lights);
        Assert.Matches(@"#ifdef DREAM_TERRAIN\s*[^#]*n = dreamTerrainBump\(n,[^;]*;\s*#endif", main);
        Assert.Contains("dreamAmbient(n) + dreamDirectShaded(n, fragPos)", main);
    }

    [Fact]
    public void The_Blend_Gate_Requires_Map_Own_Texel_And_An_Integer_Quad_Corner()
    {
        string shader = StripComments(ReadShader());
        string albedo = Body(shader, "vec3 dreamTerrainAlbedo(");

        Assert.Contains("uTerrainBlend > 0.5 && uTerrainLayer >= 0 && n.y > 0.5 && uTerrainMapSize.x > 0.5", albedo);
        Assert.Contains("dreamTerrainTexel(cell) == uTerrainLayer + 1", albedo);
        Assert.Contains("lessThan(abs(corner - cornerCell), vec2(0.01))", albedo);
        // A carved sub-cell top (non-integer height) never blends; the test is plain arithmetic.
        Assert.Contains("bool integerTop = abs(worldPos.y - floor(worldPos.y + 0.5)) < 0.01;", albedo);
        Assert.Contains("if (integerTop && all(lessThan(abs(corner - cornerCell), vec2(0.01))) && dreamTerrainTexel(cell) == uTerrainLayer + 1)", albedo);
        // The macro variation is keyed on x/z with a small y term on every face.
        Assert.Contains("worldPos.xz + worldPos.y *", albedo);
    }

    [Fact]
    public void The_Sampler_Units_Are_Set_Once_In_Load_Terrain_Shader_And_Names_Live_In_Render_Terrain()
    {
        string core = Path.Combine(RepoRoot(), "dream", "Rendering", "Core");
        string shaders = File.ReadAllText(Path.Combine(core, "Render.Shaders.cs"));
        string terrain = File.ReadAllText(Path.Combine(core, "Render.Terrain.cs"));
        int start = shaders.IndexOf("public void LoadTerrainShader", StringComparison.Ordinal);
        Assert.True(start >= 0);
        string load = shaders[start..];

        Assert.Contains("shader.SetInt(TerrainTilesUniform, TerrainTilesUnit);", load);
        Assert.Contains("shader.SetInt(TerrainMaterialMapUniform, TerrainMaterialMapUnit);", load);
        Assert.Equal(2, Dream.Rendering.Core.Render.TerrainTilesUnit);
        Assert.Equal(3, Dream.Rendering.Core.Render.TerrainMaterialMapUnit);
        Assert.Equal("uTerrainTiles", Dream.Rendering.Core.Render.TerrainTilesUniform);
        Assert.Equal("uTerrainMaterialMap", Dream.Rendering.Core.Render.TerrainMaterialMapUniform);
        Assert.DoesNotContain("\"uTerrain", shaders);
        foreach (string name in new[] { "uTerrainBlend", "uTerrainBlendBand", "uTerrainBlendDepth", "uTerrainRelief", "uTerrainLayer" })
        {
            Assert.Contains($"\"{name}\"", terrain);
        }
    }

    [Fact]
    public void Dynamic_Cubes_Draw_The_Plain_Look_And_The_Static_Pass_Restores_Unit_Zero()
    {
        string terrain = File.ReadAllText(Path.Combine(RepoRoot(), "dream", "Rendering", "Core", "Render.Terrain.cs"));
        string bind = Body(terrain, "private void BindTerrainMaterial(");
        string begin = Body(terrain, "public void BeginStaticTerrainPass(");

        Assert.Contains("shader.SetFloat(\"uTerrainVariation\", 0f);", bind);
        Assert.Contains("shader.SetFloat(\"uTerrainBlend\", 0f);", bind);
        Assert.Contains("shader.SetInt(\"uTerrainLayer\", -1);", bind);
        Assert.Contains("TextureTarget.Texture2DArray", begin);
        Assert.True(
            begin.LastIndexOf("GL.ActiveTexture(TextureUnit.Texture0);", StringComparison.Ordinal) >
            begin.IndexOf("TextureTarget.Texture2DArray", StringComparison.Ordinal));
    }

    private static int Depth(string text, int index)
    {
        int depth = 0;
        for (int i = 0; i < index; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}') depth--;
        }

        return depth;
    }

    // Removes every top-level `#ifdef DREAM_TERRAIN ... #endif` block, honouring nested #if/#endif.
    private static string StripTerrainBlocks(string source)
    {
        System.Text.StringBuilder result = new();
        int depth = 0;
        bool inTerrain = false;
        foreach (string line in source.Split('\n'))
        {
            string trimmed = line.TrimStart();
            bool opens = trimmed.StartsWith("#if", StringComparison.Ordinal);
            if (!inTerrain && opens && trimmed.StartsWith("#ifdef DREAM_TERRAIN", StringComparison.Ordinal))
            {
                inTerrain = true;
                depth = 1;
                continue;
            }

            if (inTerrain)
            {
                if (opens) depth++;
                else if (trimmed.StartsWith("#endif", StringComparison.Ordinal) && --depth == 0) inTerrain = false;
                continue;
            }

            result.Append(line).Append('\n');
        }

        return result.ToString();
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
