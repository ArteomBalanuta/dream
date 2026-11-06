using System.Text.RegularExpressions;
using Dream.Rendering.Core;
using Dream.Rendering.Lighting;
using Dream.Rendering.Shadows;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Shadows;

// The caster pass's contract: the order it drives its target in, that it ends the target on every path, and what it leaves
// untouched. The pass is written against IShadowPassTarget so this runs without a GL context; the GL half (ShadowMapTargets,
// D1) saves and restores framebuffer, viewport, depth test/mask/func, cull and blend, and is pinned by D1's own tests.
public class ShadowCasterPassContractTests
{
    private const int Resolution = 2048;

    private sealed class RecordingTarget : IShadowPassTarget
    {
        public List<string> Calls { get; } = new();

        public List<ShadowCasterView> Views { get; } = new();

        public int ThrowOnDraw { get; set; } = -1;

        public int Draws { get; private set; }

        public int DrawsPerTarget { get; set; }

        public void BeginSun() => Calls.Add("sun");

        public void BeginFireTile(int slot, int face) => Calls.Add($"fire {slot}.{face}");

        public void DrawCasters(in ShadowCasterView view)
        {
            Calls.Add("draw");
            Views.Add(view);
            if (Draws++ == ThrowOnDraw)
            {
                throw new InvalidOperationException("caster failed");
            }

            RenderCounters.CountDraws(DrawsPerTarget);
        }

        public void End() => Calls.Add("end");
    }

    private static ShadowFrame Frame(int fires, bool sun = true)
    {
        ShadowFrame frame = sun
            ? ShadowFrame.ForSun(new Vector3(5f, 0f, 5f), Vector3.Normalize(new Vector3(0.4f, 0.8f, 0.45f)), 0.8f, Resolution, 0f)
            : ShadowFrame.None;
        LightList list = default;
        list.Count = fires;
        for (int i = 0; i < fires; i++)
        {
            list.PositionRadius[i] = new Vector4(2f + i, 1f, 3f - i, 10f + i);
            list.Color[i] = new Vector4(1.6f, 0.99f, 0.51f, 0f);
        }

        FireShadowAtlas.FillSlots(in list, new Vector3(5f, 0f, 5f), 4, 0f, ref frame);
        return frame;
    }

    [Fact]
    public void The_Sun_Map_Renders_First_Then_Every_Fire_Tile_Slot_Major_And_The_Target_Is_Ended_Last()
    {
        RecordingTarget target = new();
        ShadowFrame frame = Frame(fires: 2);

        ShadowCasterPass.Run(target, in frame, new Vector3(5f, 0f, 5f));

        string[] expected =
        [
            "sun", "draw",
            "fire 0.0", "draw", "fire 0.1", "draw", "fire 0.2", "draw", "fire 0.3", "draw",
            "fire 1.0", "draw", "fire 1.1", "draw", "fire 1.2", "draw", "fire 1.3", "draw",
            "end",
        ];
        Assert.Equal(expected, target.Calls);
    }

    [Fact]
    public void An_Exception_Mid_Pass_Still_Ends_The_Target_Exactly_Once_And_Propagates()
    {
        // The restore proof: the pass cannot leak the bound framebuffer or the 512 x 512 viewport, whichever draw throws.
        for (int throwAt = 0; throwAt < 9; throwAt++)
        {
            RecordingTarget target = new() { ThrowOnDraw = throwAt };
            ShadowFrame frame = Frame(fires: 2);

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => ShadowCasterPass.Run(target, in frame, Vector3.Zero));

            Assert.Equal("caster failed", ex.Message);
            Assert.Equal("end", target.Calls[^1]);
            Assert.Equal(1, target.Calls.Count(c => c == "end"));
            // Nothing is drawn after the throw.
            Assert.Equal(throwAt + 1, target.Draws);
        }
    }

    [Fact]
    public void Every_Begin_Is_Paired_With_The_End_And_An_Empty_Frame_Only_Ends()
    {
        RecordingTarget target = new();
        ShadowFrame none = ShadowFrame.None;

        ShadowStatsCheck(ShadowCasterPass.Run(target, in none, Vector3.Zero));

        Assert.Equal(new[] { "end" }, target.Calls);

        static void ShadowStatsCheck(ShadowPassStats stats) => Assert.Equal(default, stats);
    }

    [Fact]
    public void A_Frame_Without_A_Sun_Renders_Only_The_Fires_And_One_Without_Fires_Only_The_Sun()
    {
        RecordingTarget fires = new();
        ShadowFrame noSun = Frame(fires: 1, sun: false);
        ShadowCasterPass.Run(fires, in noSun, Vector3.Zero);
        Assert.DoesNotContain("sun", fires.Calls);
        Assert.Equal(4, fires.Calls.Count(c => c.StartsWith("fire")));

        RecordingTarget sun = new();
        ShadowFrame noFires = Frame(fires: 0);
        ShadowCasterPass.Run(sun, in noFires, Vector3.Zero);
        Assert.Equal(new[] { "sun", "draw", "end" }, sun.Calls);
    }

    [Fact]
    public void The_Sun_Map_Is_Rendered_Through_An_Identity_View_And_The_Combined_Matrix_Even_At_Zero_Strength()
    {
        // Strength is 0 until D3 turns the maps on, but the map must still be written: gating on SunActive would leave the
        // pass dead (and unmeasurable) while nothing samples it.
        RecordingTarget target = new();
        ShadowFrame frame = Frame(fires: 0);
        Assert.False(frame.SunActive);

        ShadowCasterPass.Run(target, in frame, new Vector3(1f, 2f, 3f));

        ShadowCasterView view = target.Views.Single();
        Assert.Equal(Matrix4.Identity, view.View);
        Assert.Equal(frame.SunLightViewProjection, view.Projection);
        Assert.Equal(new Vector3(1f, 2f, 3f), view.Focus);
        Assert.True(view.Frustum.IsValid);
    }

    [Fact]
    public void Each_Fire_Face_Is_Rendered_Through_The_Slots_Flickered_Position_And_Far_Plane()
    {
        // The flicker reaches the maps: the matrices the pass renders each face with are built from the slot's position and
        // far plane, both of which come from the (already flickered) selected light. Two frames of the same fire differ.
        ShadowFrame a = Frame(fires: 1, sun: false);
        ShadowFrame b = a;
        Span<FireShadowSlot> bs = b.Fires;
        bs[0].Position += new Vector3(0.05f, 0.03f, -0.04f);
        bs[0].Far *= 1.04f;

        RecordingTarget first = new();
        RecordingTarget second = new();
        ShadowCasterPass.Run(first, in a, Vector3.Zero);
        ShadowCasterPass.Run(second, in b, Vector3.Zero);

        FireShadowSlot slot = a.Fires[0];
        Assert.Equal(4, first.Views.Count);
        for (int face = 0; face < 4; face++)
        {
            Assert.Equal(FireShadowAtlas.FaceView(slot.Position, face), first.Views[face].View);
            Assert.Equal(FireShadowAtlas.FaceProjection(slot.Near, slot.Far), first.Views[face].Projection);
            Assert.NotEqual(first.Views[face].View, second.Views[face].View);
            Assert.NotEqual(first.Views[face].Projection, second.Views[face].Projection);
        }
    }

    [Fact]
    public void A_Degenerate_Slot_Is_Skipped_Rather_Than_Rendered_With_A_Broken_Matrix()
    {
        ShadowFrame frame = Frame(fires: 2, sun: false);
        Span<FireShadowSlot> fires = frame.Fires;
        fires[0].Far = fires[0].Near; // a dead light
        fires[1].Position = new Vector3(float.NaN, 0f, 0f);
        RecordingTarget target = new();

        ShadowPassStats stats = ShadowCasterPass.Run(target, in frame, Vector3.Zero);

        Assert.Equal(new[] { "end" }, target.Calls);
        Assert.Equal(0, stats.FireFaces);
    }

    [Fact]
    public void The_Stats_Count_The_Draw_Calls_Each_Target_Submitted()
    {
        RenderCounters.Reset();
        RecordingTarget target = new() { DrawsPerTarget = 7 };
        ShadowFrame frame = Frame(fires: 3);

        ShadowPassStats stats = ShadowCasterPass.Run(target, in frame, Vector3.Zero);

        Assert.Equal(7, stats.SunDraws);
        Assert.Equal(7 * 12, stats.FireDraws);
        Assert.Equal(12, stats.FireFaces);
        Assert.Equal(7f, stats.DrawsPerFireFace);
        Assert.Equal(7 + 84, stats.TotalDraws);
        RenderCounters.Reset();
    }

    // ---- the GL half, by source ----

    [Fact]
    public void The_Pass_Itself_Makes_No_GL_Call_So_It_Cannot_Leak_State_Of_Its_Own()
    {
        string pass = Strip(Read("dream", "Rendering", "Shadows", "ShadowCasterPass.cs"));

        Assert.DoesNotMatch(new Regex(@"\bGL\."), pass);
        // The end is in a finally.
        Assert.Matches(new Regex(@"finally\s*\{\s*target\.End\(\);\s*\}"), pass);
    }

    [Fact]
    public void The_Gl_Target_Forwards_To_ShadowMapTargets_And_Ends_It()
    {
        string render = Strip(Read("dream", "Rendering", "Core", "Render.Shadows.cs"));

        Assert.Contains("public void BeginSun() => Targets.BeginSun();", render);
        Assert.Contains("public void BeginFireTile(int slot, int face) => Targets.BeginFireTile(slot, face);", render);
        Assert.Contains("public void End() => _render._shadowTargets?.End();", render);
        // No second implementation of the state save/restore: D1's ShadowMapTargets owns the framebuffer, viewport and caps.
        Assert.DoesNotMatch(new Regex(@"GL\.(Viewport|BindFramebuffer|Enable|Disable|DepthMask|DepthFunc)"), render);
    }

    [Fact]
    public void ShadowMapTargets_Restores_Everything_The_Pass_Changes()
    {
        // D1's half of the pair, pinned here because this pass relies on it: every state Enter changes, End puts back.
        string targets = Strip(Read("dream", "Rendering", "Shadows", "ShadowMapTargets.cs"));
        string enter = targets[targets.IndexOf("private void Enter", StringComparison.Ordinal)..];
        enter = enter[..enter.IndexOf("private void RequireEnabled", StringComparison.Ordinal)];
        string end = targets[targets.IndexOf("public void End()", StringComparison.Ordinal)..];
        end = end[..end.IndexOf("public void BindForSampling", StringComparison.Ordinal)];

        (string Set, string Restore)[] pairs =
        [
            ("GL.Enable(EnableCap.DepthTest)", "SetCap(EnableCap.DepthTest, _savedDepthTest)"),
            ("GL.Disable(EnableCap.Blend)", "SetCap(EnableCap.Blend, _savedBlend)"),
            ("GL.Disable(EnableCap.CullFace)", "SetCap(EnableCap.CullFace, _savedCullFace)"),
            ("GL.DepthMask(true)", "GL.DepthMask(_savedDepthMask)"),
            ("GL.DepthFunc(DepthFunction.Less)", "GL.DepthFunc((DepthFunction)_savedDepthFunc)"),
            ("GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer)", "GL.BindFramebuffer(FramebufferTarget.Framebuffer, _savedFramebuffer)"),
            ("GL.Viewport(x, y, width, height)", "GL.Viewport(_savedViewport[0], _savedViewport[1], _savedViewport[2], _savedViewport[3])"),
        ];
        foreach ((string set, string restore) in pairs)
        {
            Assert.Contains(set, enter);
            Assert.Contains(restore, end);
        }
    }

    [Fact]
    public void PrepareShadows_Runs_After_The_Light_Selection_And_Before_Anything_Is_Uploaded()
    {
        string render = Strip(Read("dream", "Rendering", "Core", "Render.Shadows.cs"));
        string body = render[render.IndexOf("public void PrepareShadows", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("internal static ShadowFrame BuildSunFrame", StringComparison.Ordinal)];

        int select = body.IndexOf("SelectPointLights(", StringComparison.Ordinal);
        int fill = body.IndexOf("FireShadowAtlas.FillSlots(in _selectedLights", StringComparison.Ordinal);
        int write = body.IndexOf("frame.WriteSunTo(ref _frameLighting)", StringComparison.Ordinal);
        int pass = body.IndexOf("ShadowCasterPass.Render(", StringComparison.Ordinal);
        int sampling = body.IndexOf("targets.BindForSampling()", StringComparison.Ordinal);

        Assert.True(select >= 0 && select < fill, "the fire slots are chosen from the lights SelectPointLights just picked");
        Assert.True(fill < write && write < pass, "the sun matrix reaches the lighting block before the maps are rendered");
        Assert.True(pass < sampling, "the maps are bound for sampling after they are written");
        // The upload stays where it is: PrepareShadows never uploads.
        Assert.DoesNotContain("UploadFrameLighting", body);
        // The kill switch returns before any GL work.
        int guard = body.IndexOf("!ShadowSettings.Enabled", StringComparison.Ordinal);
        Assert.True(guard >= 0 && guard < select);
    }

    [Fact]
    public void Shadow_Lifetime_Is_Wired_Into_Render()
    {
        string lifetime = Read("dream", "Rendering", "Core", "Render.Lifetime.cs");

        Assert.Contains("InitializeShadows();", lifetime);
        Assert.Contains("RunDisposeStep(DisposeShadows, ref failures);", lifetime);
        // Shadow targets go before the shaders they were drawn with.
        Assert.True(
            lifetime.IndexOf("RunDisposeStep(DisposeShadows", StringComparison.Ordinal) <
            lifetime.IndexOf("RunDisposeStep(_shaders.DisposeAll", StringComparison.Ordinal));
        // The lights are collected before the shadow maps are built: shaders and the lighting block exist first.
        Assert.True(
            lifetime.IndexOf("InitializeFrameLighting();", StringComparison.Ordinal) <
            lifetime.IndexOf("InitializeShadows();", StringComparison.Ordinal));
    }

    [Fact]
    public void The_Kill_Switch_Leaves_The_Frame_Empty_And_Allocates_No_Maps()
    {
        string render = Strip(Read("dream", "Rendering", "Core", "Render.Shadows.cs"));
        string prepare = render[render.IndexOf("public void PrepareShadows", StringComparison.Ordinal)..];
        string guard = prepare[..prepare.IndexOf("Vector3 cameraPosition", StringComparison.Ordinal)];

        Assert.Contains("!ShadowSettings.Enabled", guard);
        Assert.Contains("_shadowFrame = ShadowFrame.None;", guard);
        Assert.Contains("return;", guard);

        // With the switch off the pass has no target, so a stray Render call renders nothing.
        string passTarget = render[render.IndexOf("internal IShadowPassTarget? ShadowPassTarget", StringComparison.Ordinal)..];
        passTarget = passTarget[..passTarget.IndexOf("return _shadowPassTarget;", StringComparison.Ordinal)];
        Assert.Contains("Enabled: true", passTarget);
        Assert.Contains("return null;", passTarget);
    }

    [Fact]
    public void The_Ground_Blobs_Are_Only_Skipped_When_The_Maps_Are_The_Shadow_Source()
    {
        string effects = Read("dream", "Client", "Scenes", "PlayScene.Effects.cs");

        Match call = Regex.Match(effects, @"if \(!ShadowCasterPass\.ShadowMapsReplaceBlobs\)\s*\{\s*CollectShadows\(shadows, in presentation, viewCentre\);");
        Assert.True(call.Success, "CollectShadows is conditional on the maps replacing the blobs");
        // The only call site, and the fallback list is not edited.
        Assert.Equal(1, Regex.Matches(effects, @"CollectShadows\(shadows,").Count);
        foreach (string untouched in new[] { "FxShadowList.cs", "FxShadowRules.cs", "FxInstance.cs" })
        {
            Assert.DoesNotContain("ShadowCasterPass", Read("dream", "Rendering", "Effects", untouched));
        }

        Assert.DoesNotContain("ShadowCasterPass", Read("dream", "shaders", "fxVertex.glsl"));
    }

    [Fact]
    public void The_Maps_Replace_The_Blobs_Exactly_When_Enabled_And_Sampled_And_Strength_Follows_Sampling()
    {
        // The single switch D3 flips: while MapsAreSampled is false the sun strength and every fire strength are 0 (the shaders
        // return visibility 1.0) and the blobs stay, so the frame is the frame without the pass. The relations hold either way.
        Assert.Equal(ShadowSettings.Enabled && ShadowCasterPass.MapsAreSampled, ShadowCasterPass.ShadowMapsReplaceBlobs);
        Assert.Equal(ShadowCasterPass.MapsAreSampled ? 1f : 0f, ShadowCasterPass.Strength);

        FrameLighting lighting = FrameLighting.ReferenceNoon;
        ShadowFrame sun = Render.BuildSunFrame(in lighting, Vector3.Zero, ShadowCasterPass.Strength, Resolution);
        Assert.Equal(ShadowCasterPass.MapsAreSampled, sun.SunActive);
        Assert.True(sun.SunTexelWorld > 0f, "the sun map is built and rendered either way");
    }

    [Fact]
    public void BuildSunFrame_Follows_The_Shaders_Dominant_Light_Rule_And_Has_No_Frame_When_No_Light_Is_Up()
    {
        FrameLighting lighting = FrameLighting.ReferenceNoon;
        lighting.SunDirection = new Vector4(Vector3.Normalize(new Vector3(0.3f, 0.8f, 0.2f)), 1f);
        lighting.MoonDirection = new Vector4(Vector3.Normalize(new Vector3(-0.5f, 0.6f, 0.1f)), 0.2f);

        ShadowFrame day = Render.BuildSunFrame(in lighting, Vector3.Zero, 1f, Resolution);
        ShadowFrame expectedDay = ShadowFrame.ForSun(Vector3.Zero, lighting.SunDirection.Xyz, lighting.SunDirection.Y, Resolution, 1f);
        Assert.Equal(expectedDay.SunLightViewProjection, day.SunLightViewProjection);

        // Night: the moon is stronger than the sun, and is the light.
        lighting.SunDirection = new Vector4(lighting.SunDirection.Xyz, 0f);
        lighting.MoonDirection = new Vector4(lighting.MoonDirection.Xyz, 0.4f);
        ShadowFrame night = Render.BuildSunFrame(in lighting, Vector3.Zero, 1f, Resolution);
        ShadowFrame expectedNight = ShadowFrame.ForSun(Vector3.Zero, lighting.MoonDirection.Xyz, lighting.MoonDirection.Y, Resolution, 1f);
        Assert.Equal(expectedNight.SunLightViewProjection, night.SunLightViewProjection);
        Assert.NotEqual(day.SunLightViewProjection, night.SunLightViewProjection);

        // No light up: both off, or the dominant one below the horizon.
        lighting.MoonDirection = new Vector4(lighting.MoonDirection.Xyz, 0f);
        Assert.Equal(0f, Render.BuildSunFrame(in lighting, Vector3.Zero, 1f, Resolution).SunTexelWorld);
        lighting.SunDirection = new Vector4(0.3f, -0.2f, 0.4f, 1f);
        Assert.Equal(0f, Render.BuildSunFrame(in lighting, Vector3.Zero, 1f, Resolution).SunTexelWorld);
    }

    [Fact]
    public void The_Depth_Fragment_Stage_Still_Discards_Every_MASK_Fragment_So_Skipping_MASK_Draws_Is_Equivalent()
    {
        // AnimatedModel's depth-only mode does not draw MASK primitives at all, which is only equal to drawing them with the
        // discarding variant while the variant discards unconditionally. If depthFragment.glsl ever gains a real alpha test,
        // this fails and the skip in AnimatedModel (depthOnly) has to go.
        string fragment = Read("dream", "shaders", "depthFragment.glsl");
        Assert.Matches(new Regex(@"#ifdef WORLD_ALPHA_MASK\s*void main\(\) \{ discard; \}\s*#else\s*void main\(\) \{ \}\s*#endif"), fragment);

        string model = Strip(Read("dream", "Rendering", "Models", "AnimatedModel.cs"));
        Assert.Equal(2, Regex.Matches(model, @"if \(_depthOnly && UsesAlphaMaskProgram\(material\.AlphaMode\)\)\s*\{\s*continue;\s*\}").Count);
        Assert.Equal(2, Regex.Matches(model, @"if \(!_depthOnly\)\s*\{\s*SetMaterialProperties\(program, material\);\s*\}").Count);
        // Self-resetting: EndRender clears it, and BeginRender sets it afresh from its parameter.
        Assert.Matches(new Regex(@"public void BeginDepthRender\(Shader shader, Matrix4 view, Matrix4 projection\)\s*\{\s*BeginRender\(shader, view, projection, enableCulling: false\);\s*_depthOnly = true;"), model);
        Assert.Matches(new Regex(@"public void BeginRender\(Shader shader, Matrix4 view, Matrix4 projection, bool enableCulling = true\)\s*\{\s*ThrowIfDisposed\(\);\s*_depthOnly = false;"), model);
        Assert.Matches(new Regex(@"public void EndRender\(\)\s*\{\s*_depthOnly = false;"), model);
    }

    [Fact]
    public void Every_Caster_Draws_With_The_Shadow_Program_Depth_Only_And_Culling_Off()
    {
        string[] files =
        [
            "Rendering/Shadows/ShadowCasterPass.cs",
            "Rendering/Props/PropRenderer.cs",
            "Rendering/Ground/GroundScatterRenderer.cs",
            "Rendering/Core/Render.Shadows.cs",
        ];
        foreach (string file in files)
        {
            string text = Strip(Read(["dream", .. file.Split('/')]));
            string caster = text.Contains("DrawCasters", StringComparison.Ordinal)
                ? text[text.IndexOf("DrawCasters", StringComparison.Ordinal)..]
                : text;
            // Every caster begins with BeginDepthRender (culling off, depth only); none uses the visible BeginRender.
            Assert.DoesNotMatch(new Regex(@"\.BeginRender\("), caster);
            Assert.Contains("BeginDepthRender(", caster);
        }
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

    // Source without comments, so a regex cannot be satisfied (or defeated) by prose.
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
