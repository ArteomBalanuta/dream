using System.Text.RegularExpressions;
using Dream.Rendering.Ground;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Shadows;

// The shadow of a swaying plant must sway with the plant. cubeVertex.glsl displaces a vertex by uWind (and the sway clock in
// the FrameLighting block); the shadowDepth program is built from that same file, so the displacement CODE is shared. What is
// not shared by construction is the uniform VALUE, so these pin that the caster sets uWind from the same function the visible
// draw uses, in the same order, and resets it on every path, exactly as GroundScatterRenderer.Draw does. The GL draw itself
// needs a context, so the discipline is pinned on the source and the value on the function.
public class ShadowWindParityTests
{
    private static GroundAsset Asset(GroundAssetClass assetClass, float height, GroundLayer layer = GroundLayer.Landmark) =>
        new("a", "x.glb", assetClass, layer, 1f, 1f, height, [], 1f, 1f, 1f);

    [Fact]
    public void The_Caster_And_The_Visible_Draw_Compute_The_Same_Wind_For_Every_Asset()
    {
        foreach (GroundAssetClass assetClass in Enum.GetValues<GroundAssetClass>())
        {
            foreach (float height in new[] { 0.05f, 0.4f, 1.5f, 8f })
            {
                GroundAsset asset = Asset(assetClass, height);

                // The visible draw sends GroundWind.Uniform(class, height); the caster sends WindFor. They are one function.
                Assert.Equal(GroundWind.Uniform(assetClass, height), GroundScatterRenderer.WindFor(asset));
            }
        }

        // And it is not trivially zero: a tree sways, a rock does not.
        Assert.NotEqual(Vector2.Zero, GroundScatterRenderer.WindFor(Asset(GroundAssetClass.Tree, 8f)));
        Assert.Equal(Vector2.Zero, GroundScatterRenderer.WindFor(Asset(GroundAssetClass.Rock, 1f)));
        Assert.Equal(Vector2.Zero, GroundScatterRenderer.WindFor(Asset(GroundAssetClass.Tree, 0f)));
    }

    [Fact]
    public void There_Is_Exactly_One_Wind_Definition_And_Both_Draws_Use_It()
    {
        string source = Strip(Read("dream", "Rendering", "Ground", "GroundScatterRenderer.cs"));

        Assert.Single(Regex.Matches(source, @"GroundWind\.Uniform\("));
        Assert.Equal(2, Regex.Matches(source, @"Vector2 wind = WindFor\(info\);").Count);
        Assert.Contains("public static Vector2 WindFor(GroundAsset asset) => GroundWind.Uniform(asset.Class, asset.Height);", source);
    }

    [Fact]
    public void The_Caster_Sets_The_Wind_Before_It_Draws_And_Zeroes_The_Model_After()
    {
        string draw = Method("public void Draw(");
        string caster = Method("public int DrawCasters(");

        // The same four-step sequence in both: program uniform, model wind, draw, clear the model.
        Assert.Matches(
            new Regex(@"\(terrain \? _terrainWind : _actorWind\)\.Set\(wind\);\s*model\.Wind = wind;\s*drawnModel = model;\s*model\.BeginRender\([^;]*\);\s*model\.RenderPreparedInstanced\(shader, _matrices, count\);\s*model\.EndRender\(\);\s*model\.Wind = Vector2\.Zero;\s*drawnModel = null;"),
            draw);
        Assert.Matches(
            new Regex(@"_render\.ShadowWind\.Set\(wind\);\s*model\.Wind = wind;\s*drawnModel = model;\s*model\.BeginDepthRender\([^;]*\);\s*model\.RenderPreparedInstanced\(shader, _casterMatrices, count\);\s*model\.EndRender\(\);\s*model\.Wind = Vector2\.Zero;\s*drawnModel = null;"),
            caster);
    }

    [Fact]
    public void The_Caster_Resets_Both_The_Program_And_The_Model_In_A_Finally()
    {
        string draw = Method("public void Draw(");
        string caster = Method("public int DrawCasters(");

        // The visible draw's finally, then the caster's: same two resets, and the model is cleared only if left dirty.
        Assert.Matches(
            new Regex(@"finally\s*\{[^}]*_terrainWind\.Reset\(\);\s*_actorWind\.Reset\(\);[^}]*if \(drawnModel != null\)\s*\{\s*drawnModel\.Wind = Vector2\.Zero;\s*\}\s*\}"),
            draw);
        Assert.Matches(
            new Regex(@"finally\s*\{\s*_render\.ShadowWind\.Reset\(\);\s*if \(drawnModel != null\)\s*\{\s*drawnModel\.Wind = Vector2\.Zero;[^}]*\}\s*\}"),
            caster);

        // Every Set sits inside the try that this finally closes: nothing between the method start and the try sets the wind.
        int tryAt = caster.IndexOf("try", StringComparison.Ordinal);
        int setAt = caster.IndexOf("_render.ShadowWind.Set(", StringComparison.Ordinal);
        int finallyAt = caster.IndexOf("finally", StringComparison.Ordinal);
        Assert.True(tryAt >= 0 && tryAt < setAt && setAt < finallyAt);
    }

    [Fact]
    public void The_Wind_Uniform_Goes_To_The_Shadow_Program_And_Its_Mask_Variant()
    {
        string source = Strip(Read("dream", "Rendering", "Ground", "GroundScatterRenderer.cs"));

        // One shared writer for all three programs: the program and its alpha-mask variant, by the same ApplyWind. The shadow
        // program's tracker lives with the program, on Render.
        string shadows = Regex.Replace(Strip(Read("dream", "Rendering", "Core", "Render.Shadows.cs")), @"\s+", " ");
        Assert.Contains("new WindUniform( wind => GroundScatterRenderer.ApplyShadowWind(GetShader(ShadowDepthShaderName), wind));", shadows);
        string forward = source[source.IndexOf("internal static void ApplyShadowWind", StringComparison.Ordinal)..];
        Assert.StartsWith("internal static void ApplyShadowWind(Shader shader, Vector2 wind) => ApplyWind(shader, wind);", forward);
        Assert.Contains("_terrainWind = new WindUniform(wind => ApplyWind(_render.GetShader(TerrainShaderName), wind));", source);
        string apply = source[source.IndexOf("private static void ApplyWind", StringComparison.Ordinal)..];
        apply = apply[..Math.Min(apply.Length, 420)];
        Assert.Contains("shader.SetVector2(WindUniformName, wind);", apply);
        Assert.Contains("maskProgram.SetVector2(WindUniformName, wind);", apply);
    }

    [Fact]
    public void The_Reset_Reaches_The_Program_On_The_Exception_Path_Too()
    {
        // The caster's pattern against a recording WindUniform: set per asset, throw mid-draw, finally reset.
        List<Vector2> sent = new();
        WindUniform uniform = new(sent.Add);
        Vector2 tree = GroundScatterRenderer.WindFor(Asset(GroundAssetClass.Tree, 8f));

        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            try
            {
                uniform.Set(tree);
                throw new InvalidOperationException("draw failed");
            }
            finally
            {
                uniform.Reset();
            }
        }));

        Assert.Equal(new[] { tree, Vector2.Zero }, sent);
        Assert.Equal(Vector2.Zero, uniform.Current);

        // A reset after an asset that did not sway sends nothing (the program never left zero).
        sent.Clear();
        WindUniform still = new(sent.Add);
        still.Set(GroundScatterRenderer.WindFor(Asset(GroundAssetClass.Rock, 1f)));
        still.Reset();
        Assert.Empty(sent);
    }

    [Fact]
    public void Decals_Never_Cast_And_Nothing_Else_Is_Skipped_By_Layer()
    {
        string caster = Method("public int DrawCasters(");

        Assert.Contains("if (info.Layer != GroundLayer.Decal)", caster);
        Assert.DoesNotContain("GroundLayer.Cover", caster);
        Assert.DoesNotContain("GroundLayer.Accent", caster);
        Assert.DoesNotContain("GroundLayer.Landmark", caster);
    }

    [Fact]
    public void Props_Do_Not_Sway_And_Draw_After_The_Scatter_Has_Reset_The_Wind()
    {
        string props = Strip(Read("dream", "Rendering", "Props", "PropRenderer.cs"));
        string caster = props[props.IndexOf("public int DrawCasters(", StringComparison.Ordinal)..];
        caster = caster[..caster.IndexOf("public static void CasterBounds", StringComparison.Ordinal)];

        Assert.DoesNotContain("Wind", caster);
        Assert.DoesNotContain("uWind", caster);
        // The scatter caster (which sets and resets the wind) is the first call, so the props see uWind == 0.
        Assert.True(caster.IndexOf("_ground.DrawCasters(in view);", StringComparison.Ordinal) < caster.IndexOf("RenderPrepared(", StringComparison.Ordinal));
    }

    [Fact]
    public void The_Shadow_Program_Is_Built_From_The_Same_Vertex_File_And_The_Frame_Clock_Is_Current()
    {
        // One displacement function in the repo: shadowDepth compiles cubeVertex.glsl, and uWind is used in no other shader.
        string shaders = Read("dream", "Rendering", "Core", "Render.Shaders.cs");
        Assert.Matches(
            new Regex(@"LoadWorldShader\(ShadowDepthShaderName,\s*AssetPaths\.Shader\(""cubeVertex\.glsl""\),\s*AssetPaths\.Shader\(""depthFragment\.glsl""\)\);"),
            shaders);
        foreach (string file in Directory.GetFiles(Path.Combine(RepoRoot(), "dream", "shaders"), "*.glsl"))
        {
            bool usesWind = Regex.IsMatch(File.ReadAllText(file), @"uWind");
            Assert.Equal(Path.GetFileName(file) == "cubeVertex.glsl", usesWind);
        }

        // The sway clock is in the FrameLighting block, which still holds last frame's value when the caster runs unless the
        // frame's lighting is uploaded first; PrepareShadows does that, after the sun is written and before the pass.
        string render = Strip(Read("dream", "Rendering", "Core", "Render.Shadows.cs"));
        string body = render[render.IndexOf("public void PrepareShadows", StringComparison.Ordinal)..];
        int write = body.IndexOf("frame.WriteSunTo(ref _frameLighting)", StringComparison.Ordinal);
        int upload = body.IndexOf("_frameLightingBuffer?.Upload(in _frameLighting, cameraPosition, in view, FxClockSeconds());", StringComparison.Ordinal);
        int pass = body.IndexOf("ShadowCasterPass.Render(", StringComparison.Ordinal);
        Assert.True(write >= 0 && write < upload && upload < pass);
    }

    private static string Method(string signature)
    {
        string source = Strip(Read("dream", "Rendering", "Ground", "GroundScatterRenderer.cs"));
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        int next = source.IndexOf("\n    public ", start + signature.Length, StringComparison.Ordinal);
        return source[start..(next < 0 ? source.Length : next)];
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

    private static string Strip(string source) => Regex.Replace(source, @"//[^\r\n]*", string.Empty);

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
