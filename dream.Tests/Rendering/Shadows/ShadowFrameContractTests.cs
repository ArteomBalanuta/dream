using System.Globalization;
using System.Text.RegularExpressions;
using Dream.Rendering.Lighting;
using Dream.Rendering.Shadows;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Shadows;

// The D1 contracts that are not maths: the ShadowFrame shape D2 and D3 compile against, the GLSL that must stay inert
// until D3 turns it on, the depth program that must reuse cubeVertex.glsl verbatim, and the texture setup.
public class ShadowFrameContractTests
{
    [Fact]
    public void None_Is_Inert_Strength_Zero_With_No_Fires()
    {
        ShadowFrame none = ShadowFrame.None;

        Assert.False(none.SunActive);
        Assert.Equal(0f, none.SunParams.W);
        Assert.Equal(0, none.FireCount);
    }

    [Fact]
    public void A_Frame_Holds_Exactly_Four_Fire_Slots_With_The_Contracted_Fields()
    {
        ShadowFrame frame = ShadowFrame.None;
        Assert.Equal(ShadowSettings.MaxCastingFireCount, 4);
        Assert.Equal(4, ((ReadOnlySpan<FireShadowSlot>)frame.Fires).Length);

        Span<FireShadowSlot> fires = frame.Fires;
        fires[3] = new FireShadowSlot { LightIndex = 7, Position = new Vector3(1f, 2f, 3f), Near = 0.1f, Far = 11.5f, Strength = 0.8f };
        frame.FireCount = 4;

        Assert.Equal(7, frame.Fires[3].LightIndex);
        Assert.Equal(new Vector3(1f, 2f, 3f), frame.Fires[3].Position);
        Assert.Equal(11.5f, frame.Fires[3].Far);
        Assert.Equal(0, frame.Fires[0].LightIndex);
    }

    [Fact]
    public void The_Sun_Half_Is_Written_Into_The_Lighting_Block_As_Texture_Matrix_Rows_And_Params()
    {
        ShadowFrame frame = ShadowFrame.ForSun(new Vector3(2f, 0f, 3f), Vector3.Normalize(new Vector3(1f, 2f, 1f)), 0.8f, 2048, 1f);
        FrameLighting lighting = FrameLighting.ReferenceNoon;

        frame.WriteSunTo(ref lighting);

        Assert.True(frame.SunActive);
        Assert.Equal(frame.SunTextureMatrix.Row0, lighting.SunShadowMatrix0);
        Assert.Equal(frame.SunTextureMatrix.Row1, lighting.SunShadowMatrix1);
        Assert.Equal(frame.SunTextureMatrix.Row2, lighting.SunShadowMatrix2);
        Assert.Equal(frame.SunTextureMatrix.Row3, lighting.SunShadowMatrix3);
        Assert.Equal(frame.SunParams, lighting.SunShadowParams);
        Assert.Equal(1f, lighting.SunShadowParams.W);
    }

    [Fact]
    public void A_Zero_Strength_Frame_Writes_A_Zero_Strength_Block()
    {
        ShadowFrame frame = ShadowFrame.ForSun(Vector3.Zero, Vector3.UnitY, 1f, 2048, 0f);
        FrameLighting lighting = FrameLighting.ReferenceNoon;

        frame.WriteSunTo(ref lighting);

        Assert.False(frame.SunActive);
        Assert.Equal(0f, lighting.SunShadowParams.W);
    }

    // ---- lighting.glsl ----

    [Fact]
    public void Direct_Is_Textually_Unchanged_The_Engine_Contract()
    {
        string lighting = Normalise(Read("dream", "shaders", "lighting.glsl"));
        const string direct =
            "vec3 dreamDirect(vec3 n) {\n" +
            "    return max(dot(n, uSunDirection.xyz), 0.0) * uSunDirection.w * uSunColor.rgb\n" +
            "         + max(dot(n, uMoonDirection.xyz), 0.0) * uMoonDirection.w * uMoonColor.rgb;\n" +
            "}\n";
        Assert.Contains(direct, lighting);
        Assert.Contains("vec3 dreamAmbient(vec3 n) { return mix(uAmbientGround.rgb, uAmbientSky.rgb, n.y * 0.5 + 0.5); }", lighting);
    }

    [Fact]
    public void The_Sun_Visibility_Is_Live_And_Compiles_To_A_Literal_One_With_No_Fetch_With_The_Switch_Off()
    {
        string lighting = Normalise(Read("dream", "shaders", "lighting.glsl"));
        Assert.Matches(@"#define DREAM_SUN_SHADOW_LIVE 1\n", lighting); // D3 threw the switch; the 0 build is still pinned below

        Match function = Regex.Match(lighting, @"float dreamSunVisibility\(vec3 fragPos, vec3 n\)\n\{\n(.*?)\n\}\n", RegexOptions.Singleline);
        Assert.True(function.Success, "dreamSunVisibility(vec3 fragPos, vec3 n) not found.");
        string body = function.Groups[1].Value;
        Assert.Contains("#if DREAM_SUN_SHADOW_LIVE", body);

        // What the compiler sees with the switch at 0 (not the shipped value): everything between #if and #else is gone.
        string inert = Regex.Replace(body, @"#if DREAM_SUN_SHADOW_LIVE\n.*?#else\n", string.Empty, RegexOptions.Singleline);
        inert = inert.Replace("#endif", string.Empty);
        Assert.Equal("return 1.0;", inert.Trim());
        Assert.DoesNotContain("texture", inert);
        Assert.DoesNotContain("uSunShadow", inert);
    }

    [Fact]
    public void The_Sun_Visibility_Is_Multiplied_Into_The_Dominant_Lights_Term_At_Exactly_One_Place()
    {
        // D1 shipped pixel-identical: nothing outside its own definition called the function. D3 added the one call in
        // clouds.glsl's dreamDirectShaded, on the dominant light's term.
        foreach (string file in Directory.GetFiles(Path.Combine(RepoRoot(), "dream", "shaders"), "*.glsl"))
        {
            string text = Regex.Replace(File.ReadAllText(file), @"//[^
]*", string.Empty);
            int calls = Regex.Matches(text, @"dreamSunVisibility\s*\(").Count;
            // The sculpted-equipment wave added the third and last caller: enemyFragment.glsl gates
            // the equipment SPECULAR on the same visibility, so a pauldron does not glint while the
            // figure stands in a doorway's shadow. Still exactly one call per file, still only on
            // the dominant light's term, and still nothing else in the tree calls it.
            if (Path.GetFileName(file) is "lighting.glsl" or "clouds.glsl" or "enemyFragment.glsl")
            {
                Assert.Equal(1, calls);
            }
            else
            {
                Assert.Equal(0, calls);
            }
        }
    }

    [Fact]
    public void The_Glsl_Matrix_Is_Built_From_The_Four_Members_In_Order_With_No_Transpose()
    {
        string lighting = Normalise(Read("dream", "shaders", "lighting.glsl"));
        Assert.Contains("mat4(uSunShadowMatrix0, uSunShadowMatrix1, uSunShadowMatrix2, uSunShadowMatrix3)", lighting);
        Assert.DoesNotContain("transpose(", lighting);
        Assert.Contains("uniform sampler2DShadow uSunShadowMap;", lighting);
    }

    [Fact]
    public void The_Glsl_Bias_Constants_Equal_The_Csharp_Constants()
    {
        string lighting = Read("dream", "shaders", "lighting.glsl");
        Assert.Equal(ShadowSettings.GrazingCosineFloor, GlslFloat(lighting, "SunShadowGrazingFloor"), 5);
        Assert.Equal(ShadowSettings.SlopeTanMax, GlslFloat(lighting, "SunShadowTanMax"), 5);
        Assert.Equal(ShadowSettings.SlopePerInverseResolution, GlslFloat(lighting, "SunShadowSlopePerInvRes"), 1);
        Assert.Equal(MathF.Sqrt(ShadowSettings.SunTaps), Match(lighting, @"const int SunShadowTapsPerSide = (\d+);"), 5);
    }

    private static float GlslFloat(string source, string name) => Match(source, @"const float " + name + @" = ([0-9.]+);");

    private static float Match(string source, string pattern)
    {
        Match m = Regex.Match(source, pattern);
        Assert.True(m.Success, pattern);
        return float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    // ---- depth program ----

    [Fact]
    public void The_Depth_Program_Reuses_The_Cube_Vertex_Shader_Verbatim_So_The_Wind_Cannot_Detach_Shadows()
    {
        string shaders = Read("dream", "Rendering", "Core", "Render.Shaders.cs");

        Assert.Matches(
            @"LoadWorldShader\(ShadowDepthShaderName,\s*AssetPaths\.Shader\(""cubeVertex\.glsl""\),\s*AssetPaths\.Shader\(""depthFragment\.glsl""\)\);",
            shaders);
        Assert.Equal("shadowDepth", Dream.Rendering.Core.Render.ShadowDepthShaderName);
        // The only vertex shader this file names is cubeVertex.glsl: no second one.
        foreach (Match m in Regex.Matches(shaders, @"Shader\(""(\w+Vertex\.glsl)""\)"))
        {
            Assert.Contains(m.Groups[1].Value, new[] { "vertexShader.glsl", "cubeVertex.glsl" });
        }

        Assert.False(File.Exists(Path.Combine(RepoRoot(), "dream", "shaders", "depthVertex.glsl")));
        // The wind sway lives in the vertex shader the depth program reuses.
        Assert.Contains("uWind", Read("dream", "shaders", "cubeVertex.glsl"));
    }

    [Fact]
    public void The_Shadow_Samplers_Are_Pinned_To_Units_5_And_6_On_Every_World_Program_And_Its_Mask_Variant()
    {
        string shaders = Read("dream", "Rendering", "Core", "Render.Shaders.cs");

        Assert.Matches(@"shader\.SetInt\(ShadowSettings\.SunMapUniform, ShadowSettings\.SunTextureUnit\);\s*shader\.SetInt\(ShadowSettings\.AtlasUniform, ShadowSettings\.AtlasTextureUnit\);", shaders);
        // LoadWorldShader binds the opaque program AND its AlphaMaskVariant; the terrain program binds its own.
        Assert.Matches(@"BindShadowSamplers\(shader\);\s*if \(shader\.AlphaMaskVariant != null\)\s*\{\s*BindShadowSamplers\(shader\.AlphaMaskVariant\);", shaders);
        Assert.Matches(@"shader\.SetInt\(TerrainMaterialMapUniform, TerrainMaterialMapUnit\);\s*BindShadowSamplers\(shader\);", shaders);
        Assert.Equal("uSunShadowMap", ShadowSettings.SunMapUniform);
    }

    [Fact]
    public void The_Depth_Fragment_Is_Empty_And_Its_Mask_Variant_Discards_Everything()
    {
        string text = Normalise(Read("dream", "shaders", "depthFragment.glsl"));

        Assert.StartsWith("#version 330 core", text);
        Assert.Matches(@"#ifdef WORLD_ALPHA_MASK\nvoid main\(\) \{ discard; \}\n#else\nvoid main\(\) \{ \}\n#endif", text);
    }

    // ---- ShadowMapTargets ----

    [Fact]
    public void The_Maps_Are_Configured_For_Hardware_Compare_Filtering_And_Lit_Outside_The_Volume()
    {
        string targets = Read("dream", "Rendering", "Shadows", "ShadowMapTargets.cs");

        Assert.Contains("PixelInternalFormat.DepthComponent32f, sunResolution", targets);
        Assert.Contains("PixelInternalFormat.DepthComponent24, atlasResolution", targets);
        Assert.Contains("TextureCompareMode.CompareRefToTexture", targets);
        Assert.Contains("All.Lequal", targets);
        Assert.Contains("TextureMinFilter.Linear", targets);
        Assert.Contains("TextureMagFilter.Linear", targets);
        Assert.Contains("TextureWrapMode.ClampToBorder", targets);
        Assert.Contains("[1f, 1f, 1f, 1f]", targets);
        // The always-valid dummy for the disabled case.
        Assert.Contains("PixelInternalFormat.DepthComponent16, 1, 1", targets);
        Assert.Contains("_enabled ? _sunTexture : _dummyTexture", targets);
        Assert.Contains("_enabled ? _atlasTexture : _dummyTexture", targets);
    }

    [Fact]
    public void The_Caster_State_Restores_Framebuffer_And_Viewport()
    {
        string targets = Read("dream", "Rendering", "Shadows", "ShadowMapTargets.cs");

        Assert.Contains("GL.GetInteger(GetPName.Viewport, _savedViewport);", targets);
        Assert.Contains("GL.Viewport(_savedViewport[0], _savedViewport[1], _savedViewport[2], _savedViewport[3]);", targets);
        Assert.Contains("GL.BindFramebuffer(FramebufferTarget.Framebuffer, _savedFramebuffer);", targets);
        Assert.Contains("GL.Disable(EnableCap.CullFace);", targets);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 3)]
    [InlineData(1, 0)]
    [InlineData(3, 3)]
    public void The_Fire_Tiles_Are_Slot_Major_Row_Major_In_The_Four_By_Four_Grid(int slot, int face)
    {
        (int x, int y) = ShadowMapTargets.TileOrigin(slot, face, 512);

        int index = slot * 4 + face;
        Assert.Equal(index % 4 * 512, x);
        Assert.Equal(index / 4 * 512, y);
        // Distinct tiles, all inside the atlas.
        Assert.InRange(x, 0, 2048 - 512);
        Assert.InRange(y, 0, 2048 - 512);
    }

    [Fact]
    public void Every_Slot_Face_Pair_Gets_A_Distinct_Tile_And_Out_Of_Range_Is_Rejected()
    {
        HashSet<(int, int)> seen = [];
        for (int slot = 0; slot < 4; slot++)
        {
            for (int face = 0; face < 4; face++)
            {
                Assert.True(seen.Add(ShadowMapTargets.TileOrigin(slot, face, 512)));
            }
        }

        Assert.Equal(16, seen.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => ShadowMapTargets.TileOrigin(4, 0, 512));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShadowMapTargets.TileOrigin(0, 4, 512));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShadowMapTargets.TileOrigin(-1, 0, 512));
    }

    private static string Normalise(string text) => text.Replace("\r\n", "\n").TrimStart('﻿');

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

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
