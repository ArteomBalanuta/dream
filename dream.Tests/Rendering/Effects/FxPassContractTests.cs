using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Dream.Rendering.Effects;
using Dream.Tests.Utilities;
using Xunit;

namespace Dream.Tests.Rendering.Effects;

// GL is unavailable in tests, so the effects pass is checked through its sources: the shader
// contract, the instance layout, the GL state it sets and restores, and its place in the frame.
public class FxPassContractTests
{
    private static readonly Regex UniformDeclaration = new(@"\buniform\s+\w+\s+(\w+)\s*;");

    [Fact]
    public void Instance_Is_Thirty_Six_Bytes_With_The_Documented_Offsets()
    {
        Assert.Equal(36, Unsafe.SizeOf<FxInstance>());
        Assert.Equal(FxInstance.Stride, Unsafe.SizeOf<FxInstance>());
        Assert.Equal(0, (int)Marshal.OffsetOf<FxInstance>(nameof(FxInstance.Position)));
        Assert.Equal(12, (int)Marshal.OffsetOf<FxInstance>(nameof(FxInstance.HalfSize)));
        Assert.Equal(FxInstance.ColorOffset, (int)Marshal.OffsetOf<FxInstance>(nameof(FxInstance.Color)));
        Assert.Equal(FxInstance.RotationOffset, (int)Marshal.OffsetOf<FxInstance>(nameof(FxInstance.Rotation)));
        Assert.Equal(24, (int)Marshal.OffsetOf<FxInstance>(nameof(FxInstance.Frame)));
        Assert.Equal(28, (int)Marshal.OffsetOf<FxInstance>(nameof(FxInstance.DepthBias)));
        Assert.Equal(32, (int)Marshal.OffsetOf<FxInstance>(nameof(FxInstance.Stretch)));
    }

    [Fact]
    public void Vertex_Attribute_Locations_Match_The_Instance_Layout()
    {
        string vertex = StripComments(ReadShader(FxRenderer.VertexShaderFile));
        Assert.Matches(@"layout\(location = 0\) in vec4 iPositionHalfSize;", vertex);
        Assert.Matches(@"layout\(location = 1\) in vec4 iColor;", vertex);
        Assert.Matches(@"layout\(location = 2\) in vec4 iRotationFrameBiasStretch;", vertex);
        Assert.Equal(0, FxInstance.PositionHalfSizeLocation);
        Assert.Equal(1, FxInstance.ColorLocation);
        Assert.Equal(2, FxInstance.RotationFrameBiasLocation);

        string bind = Method(ReadSource("dream/Rendering/Effects/FxInstanceBuffer.cs"), "public void BindAttributes()");
        Assert.Contains("VertexAttribPointer(FxInstance.PositionHalfSizeLocation, 4, VertexAttribPointerType.Float, false, FxInstance.Stride, 0);", bind);
        Assert.Contains("VertexAttribPointer(FxInstance.ColorLocation, 4, VertexAttribPointerType.UnsignedByte, true, FxInstance.Stride, FxInstance.ColorOffset);", bind);
        Assert.Contains("VertexAttribPointer(FxInstance.RotationFrameBiasLocation, 4, VertexAttribPointerType.Float, false, FxInstance.Stride, FxInstance.RotationOffset);", bind);
        Assert.Equal(3, Regex.Matches(bind, @"VertexAttribDivisor\(FxInstance\.\w+, 1\);").Count);
    }

    [Fact]
    public void Instance_Buffer_Orphans_Then_Sub_Uploads_At_A_Fixed_Capacity()
    {
        string upload = Method(ReadSource("dream/Rendering/Effects/FxInstanceBuffer.cs"), "public void Upload(");

        int orphanAt = IndexOf(upload, "GL.BufferData(BufferTarget.ArrayBuffer, Capacity * FxInstance.Stride, IntPtr.Zero, BufferUsageHint.StreamDraw);");
        int subAt = IndexOf(upload, "GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, count * FxInstance.Stride, instances);");
        Assert.True(orphanAt < subAt);
        Assert.Contains("if (count == 0)", upload);
        Assert.DoesNotContain("new ", upload);
        Assert.Equal(FxLimits.MaxInstances, new FxInstanceBuffer().Capacity);
    }

    [Fact]
    public void Limits_And_Atlas_Constants_Match_The_Contract()
    {
        Assert.Equal(2048, FxLimits.MaxInstances);
        Assert.Equal(128, FxLimits.HaloBudget);
        Assert.Equal(32, FxLimits.ShadowBudget);
        Assert.Equal(1888, FxLimits.ParticleCapacity);
        Assert.Equal(FxLimits.MaxInstances - FxLimits.HaloBudget - FxLimits.ShadowBudget, FxLimits.ParticleCapacity);
        Assert.Equal(8, FxAtlas.Columns);
        Assert.Equal(4, FxAtlas.Rows);
        Assert.Equal(64, FxAtlas.CellPixels);
        Assert.Equal(2, FxAtlas.MaxMipLevel);
        Assert.Equal("particles/particles.png", FxAtlas.RelativePath);
        Assert.Equal(new byte[] { 0, 4, 8, 12, 16, 17, 18, 22 },
            new[] { FxFrame.Spark0, FxFrame.Ember0, FxFrame.Dust0, FxFrame.Ash0, FxFrame.Halo, FxFrame.HaloCore, FxFrame.Flame0, FxFrame.Blob }.Select(f => (byte)f));

        string vertex = StripComments(ReadShader(FxRenderer.VertexShaderFile));
        Assert.Contains($"const float AtlasColumns = {FxAtlas.Columns}.0;", vertex);
        Assert.Contains($"const float AtlasRows = {FxAtlas.Rows}.0;", vertex);
    }

    [Fact]
    public void Every_Declared_Uniform_Is_In_The_Table_And_Uploaded_Once_Per_Draw()
    {
        HashSet<string> declared = new(StringComparer.Ordinal);
        foreach (string file in new[] { FxRenderer.VertexShaderFile, FxRenderer.FragmentShaderFile })
        {
            foreach (Match match in UniformDeclaration.Matches(StripComments(ReadShader(file))))
            {
                declared.Add(match.Groups[1].Value);
            }
        }

        Assert.Equal(declared.OrderBy(name => name), FxRenderer.UniformNames.OrderBy(name => name));

        string renderer = ReadSource("dream/Rendering/Effects/FxRenderer.cs");
        string draw = Method(renderer, "public void Draw(FxInstance[] instances");
        Assert.DoesNotContain("GetUniformLocation(", draw);
        Assert.Equal(FxRenderer.UniformNames.Length, Regex.Matches(draw, @"_locations\[Location\w+\]").Count);
        Assert.Contains("_shader.GetUniformLocations(UniformNames)", renderer);
    }

    [Fact]
    public void Vertex_Shader_Builds_The_Strip_From_The_Vertex_Id_And_Never_Reads_The_Fog_Origin()
    {
        string vertex = StripComments(ReadShader(FxRenderer.VertexShaderFile));

        Assert.StartsWith("#version 330 core", vertex.TrimStart('﻿'));
        Assert.Contains("vec2(gl_VertexID & 1, gl_VertexID >> 1)", vertex);
        // The camera axes come from uniforms; in the isometric view uCameraPosition is the fog
        // origin (the focus), not the eye, so a direction derived from it would be wrong.
        Assert.DoesNotContain("uCameraPosition", vertex);
        Assert.DoesNotContain("#include", vertex);
        Assert.Matches(@"uCameraRight \* rotated\.x \+ uCameraUp \* rotated\.y", vertex);
        Assert.Matches(@"uCameraBack \* iRotationFrameBiasStretch\.z", vertex);
        Assert.Contains("scaled.x *= max(shape, 1.0);", vertex);
        Assert.Contains("shape < 0.0", vertex);
        // The atlas is y-down and uploaded unflipped.
        Assert.Matches(@"\(row \+ 1\.0 - corner\.y\) / AtlasRows", vertex);
    }

    [Fact]
    public void Fragment_Shader_Fades_With_Fog_Without_Exposure_Or_Discard()
    {
        string fragment = StripComments(ReadShader(FxRenderer.FragmentShaderFile));

        Assert.Single(Regex.Matches(fragment, @"^\s*#include ""lighting\.glsl""\s*$", RegexOptions.Multiline));
        Assert.Matches(@"FragColor = texture\(uAtlas, vUv\) \* vColor \* dreamFogTransmittance\(vWorldPos\);", fragment);
        Assert.DoesNotContain("discard", fragment);
        Assert.DoesNotContain("dreamExpose", fragment);
        Assert.DoesNotContain("dreamApplyFog", fragment);
    }

    [Fact]
    public void Draw_Sets_Its_State_Restores_It_And_Allocates_Nothing()
    {
        string draw = Method(ReadSource("dream/Rendering/Effects/FxRenderer.cs"), "public void Draw(FxInstance[] instances");

        int returnAt = IndexOf(draw, "if (count <= 0)");
        int cullQueryAt = IndexOf(draw, "bool cullWasEnabled = GL.IsEnabled(EnableCap.CullFace);");
        int depthTestAt = IndexOf(draw, "GL.Enable(EnableCap.DepthTest);");
        int writesOffAt = IndexOf(draw, "GL.DepthMask(false);");
        int blendOnAt = IndexOf(draw, "GL.Enable(EnableCap.Blend);");
        int blendFuncAt = IndexOf(draw, "GL.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);");
        int cullOffAt = IndexOf(draw, "GL.Disable(EnableCap.CullFace);");
        int textureUnitAt = IndexOf(draw, "GL.ActiveTexture(TextureUnit.Texture0);");
        int bindAt = IndexOf(draw, "GL.BindTexture(TextureTarget.Texture2D, _atlas);");
        int drawAt = IndexOf(draw, "GL.DrawArraysInstanced(PrimitiveType.TriangleStrip, 0, 4, count);");
        int countAt = IndexOf(draw, "RenderCounters.CountDraw();");
        int writesOnAt = IndexOf(draw, "GL.DepthMask(true);");
        int blendOffAt = IndexOf(draw, "GL.Disable(EnableCap.Blend);");
        int cullOnAt = IndexOf(draw, "GL.Enable(EnableCap.CullFace);");

        Assert.True(returnAt < cullQueryAt, "An empty frame returns before any state change.");
        Assert.True(cullQueryAt < depthTestAt && cullQueryAt < cullOffAt);
        Assert.True(depthTestAt < drawAt && writesOffAt < drawAt && blendOnAt < drawAt && blendFuncAt < drawAt && cullOffAt < drawAt);
        Assert.True(textureUnitAt < bindAt && bindAt < drawAt);
        Assert.True(drawAt < countAt && countAt < writesOnAt && countAt < blendOffAt && countAt < cullOnAt);
        Assert.Contains("GL.BindVertexArray(0);", draw[drawAt..]);
        Assert.DoesNotContain("GL.Disable(EnableCap.DepthTest)", draw);
        Assert.DoesNotContain("new ", draw.Replace("new ObjectDisposedException(", string.Empty));
        Assert.DoesNotContain("foreach", draw);
        Assert.DoesNotContain("=>", draw);
    }

    [Fact]
    public void Renderer_Asserts_The_Render_Thread_And_Releases_Everything()
    {
        string source = ReadSource("dream/Rendering/Effects/FxRenderer.cs");
        string constructor = Method(source, "public FxRenderer()");
        string dispose = Method(source, "public void Dispose()");

        Assert.Contains("GLUtils.AssertRenderThread(", constructor);
        Assert.Contains("TextureDecoder.DecodeRgba(AssetPaths.Ui(FxAtlas.RelativePath))", constructor);
        Assert.Contains("TextureDecoder.UploadTexture2DClamped(atlas, FxAtlas.MaxMipLevel)", constructor);
        Assert.DoesNotContain("DecodeRgbaFlipped", constructor);
        Assert.Contains("GLUtils.AssertRenderThread(", dispose);
        Assert.Contains("_shader?.Dispose();", dispose);
        Assert.Contains("GL.DeleteTexture(_atlas);", dispose);
        Assert.Contains("GL.DeleteVertexArray(_vertexArray);", dispose);
        Assert.Contains("_instances.Dispose();", dispose);
    }

    [Fact]
    public void PlayScene_Draws_The_Effects_After_The_Sky_And_Before_Debug_And_Viewmodel()
    {
        string render = Method(ReadSource("dream/Client/Scenes/PlayScene.cs"), "public void Render(FrameEventArgs args");

        int skyAt = IndexOf(render, "skyRenderer.Draw(in sky, presentation.Camera.GetViewMatrix(), _render.ProjectionMatrix);");
        int effectsAt = IndexOf(render, "DrawEffectsPass(in sky, in presentation, gpuTimer, (float)args.Time);");
        int debugAt = IndexOf(render, "_render.Debug.DrawCollisionBodies(");
        int viewModelAt = IndexOf(render, "_render.ViewModels.Draw(");
        int hudSectionAt = IndexOf(render, "gpuTimer.BeginSection(GpuTimerSection.Hud);");

        Assert.True(skyAt < effectsAt && effectsAt < debugAt && effectsAt < viewModelAt && effectsAt < hudSectionAt);
        Assert.Single(Regex.Matches(render, @"DrawEffectsPass\("));
        // Outside every branch of the render method (8 spaces), so it holds in every camera mode.
        Assert.Matches(@"(?m)^ {8}DrawEffectsPass\(in sky, in presentation, gpuTimer, \(float\)args\.Time\);\r?$", render);
        Assert.Matches(@"(?m)^ {12}GpuEffectsMilliseconds = gpuTimer\.Latest\.EffectsMilliseconds,\r?$", render);
    }

    [Fact]
    public void Effects_Pass_Advances_Writes_Then_Draws_Inside_Its_Own_Timer_Section()
    {
        string pass = Method(ReadSource("dream/Client/Scenes/PlayScene.cs"), "private void DrawEffectsPass(");

        int basisAt = IndexOf(pass, "FxViewBasis basis = FxViewBasis.FromView(in view);");
        int advanceAt = IndexOf(pass, "simulation.Advance(in inputs, _render.Lights.Lights);");
        int writeAt = IndexOf(pass, "simulation.WriteInstances(_fxInstances, shadowCount);");
        int beginAt = IndexOf(pass, "gpuTimer.BeginSection(GpuTimerSection.Effects);");
        int drawAt = IndexOf(pass, "renderer.Draw(_fxInstances, count, in basis, view * _render.ProjectionMatrix);");
        int endAt = IndexOf(pass, "gpuTimer.EndSection();");

        Assert.True(basisAt < advanceAt && advanceAt < writeAt && writeAt < beginAt && beginAt < drawAt && drawAt < endAt);
        Assert.Contains("sky.StarVisibility", pass);
        Assert.Contains("FxViewBasis.FromView(in view)", pass);
        Assert.DoesNotContain("new FxInstance", pass);
    }

    [Fact]
    public void PlayScene_Creates_The_Effects_After_The_Sky_And_Disposes_Them_In_Their_Own_Step()
    {
        string source = ReadSource("dream/Client/Scenes/PlayScene.cs");
        string constructor = Method(source, "public PlayScene(");
        string dispose = Method(source, "public void Dispose()");
        string disposeEffects = Method(source, "private void DisposeEffects()");

        int skyAt = IndexOf(constructor, "_sky = new SkyRenderer();");
        int rendererAt = IndexOf(constructor, "_fxRenderer = new FxRenderer();");
        int bridgeAt = IndexOf(constructor, "_fxBridge = new FxEventBridge(_events, _fxSimulation, _fxFlashes);");
        Assert.True(skyAt < rendererAt && rendererAt < bridgeAt);

        int disposeSkyAt = IndexOf(dispose, "RunDisposeStep(DisposeSky, ref failures);");
        int disposeEffectsAt = IndexOf(dispose, "RunDisposeStep(DisposeEffects, ref failures);");
        Assert.True(disposeSkyAt < disposeEffectsAt);
        Assert.Contains("_fxBridge?.Dispose();", disposeEffects);
        Assert.Contains("_fxRenderer?.Dispose();", disposeEffects);
        Assert.Contains("_fxRenderer = null;", disposeEffects);
    }

    [Fact]
    public void PlayScene_Runs_Without_Effects_When_The_Renderer_Cannot_Be_Created()
    {
        string source = ReadSource("dream/Client/Scenes/PlayScene.cs");
        string constructor = Method(source, "public PlayScene(");
        string pass = Method(source, "private void DrawEffectsPass(");
        string disposeEffects = Method(source, "private void DisposeEffects()");

        int tryAt = IndexOf(constructor, "try");
        int rendererAt = IndexOf(constructor, "_fxRenderer = new FxRenderer();");
        int catchAt = IndexOf(constructor, "catch (Exception exception)");
        int nullRendererAt = IndexOf(constructor, "_fxRenderer = null;");
        int nullSimulationAt = IndexOf(constructor, "_fxSimulation = null;");
        int reportAt = IndexOf(constructor, "RenderFaultLog.Shared.Report(\"effects\", exception);");
        int bridgeAt = IndexOf(constructor, "_fxBridge = new FxEventBridge(_events, _fxSimulation, _fxFlashes);");
        Assert.True(tryAt < rendererAt && rendererAt < catchAt && catchAt < nullRendererAt && nullRendererAt < nullSimulationAt);
        Assert.True(nullSimulationAt < reportAt && reportAt < bridgeAt);
        Assert.Contains("if (_fxSimulation != null && _fxFlashes != null)", constructor);

        int guardAt = IndexOf(pass, "if (_fxRenderer is not { } renderer || _fxSimulation is not { } simulation");
        int returnAt = IndexOf(pass, "return;");
        Assert.True(guardAt < returnAt && returnAt < IndexOf(pass, "simulation.Advance("));
        Assert.DoesNotContain("ObjectDisposedException", pass);
        Assert.Contains("_fxBridge?.Dispose();", disposeEffects);
        Assert.Contains("_fxRenderer?.Dispose();", disposeEffects);
    }

    [Fact]
    public void A_Failed_Effects_Start_Leaves_No_Flashes_And_No_Shadows()
    {
        string source = ReadSource("dream/Client/Scenes/PlayScene.cs");
        string constructor = Method(source, "public PlayScene(");
        string pass = Method(source, "private void DrawEffectsPass(");
        string disposeEffects = Method(source, "private void DisposeEffects()");

        int simulationAt = IndexOf(constructor, "_fxSimulation = new FxSimulation();");
        int flashesAt = IndexOf(constructor, "_fxFlashes = new FxFlashList();");
        int shadowsAt = IndexOf(constructor, "_fxShadows = new FxShadowList();");
        int catchAt = IndexOf(constructor, "catch (Exception exception)");
        int nullFlashesAt = IndexOf(constructor, "_fxFlashes = null;");
        int nullShadowsAt = IndexOf(constructor, "_fxShadows = null;");
        Assert.True(simulationAt < flashesAt && flashesAt < shadowsAt && shadowsAt < catchAt);
        Assert.True(catchAt < nullFlashesAt && nullFlashesAt < nullShadowsAt);
        Assert.Contains("_fxFlashes is not { } flashes || _fxShadows is not { } shadows", pass);
        Assert.Contains("_fxFlashes = null;", disposeEffects);
        Assert.Contains("_fxShadows = null;", disposeEffects);
        Assert.Contains("_fxFlashes?.AppendTo(lights, _fxLastNight);", source);
    }

    [Fact]
    public void Effects_Pass_Writes_The_Shadows_Before_The_Particles_And_The_Flashes_Join_The_Lights_After_The_Lamps()
    {
        string source = ReadSource("dream/Client/Scenes/PlayScene.cs");
        string pass = Method(source, "private void DrawEffectsPass(");
        string lights = Method(source, "private void CollectFrameLights(");

        int flashAdvanceAt = IndexOf(pass, "flashes.Advance(deltaSeconds);");
        int advanceAt = IndexOf(pass, "simulation.Advance(in inputs, _render.Lights.Lights);");
        int collectAt = IndexOf(pass, "CollectShadows(shadows, in presentation, viewCentre);");
        int shadowWriteAt = IndexOf(pass, "shadows.Write(_fxInstances,");
        int writeAt = IndexOf(pass, "simulation.WriteInstances(_fxInstances, shadowCount);");
        Assert.True(flashAdvanceAt < advanceAt && advanceAt < collectAt && collectAt < shadowWriteAt && shadowWriteAt < writeAt);

        Assert.True(IndexOf(lights, "CollectLampLights(lights, in presentation);") < IndexOf(lights, "CollectEffectLights(lights, remotePlayers);"));
    }

    [Fact]
    public void Bridge_Unsubscribes_Both_Handlers_And_Ignores_Zero_Damage()
    {
        string source = ReadSource("dream/Rendering/Effects/FxEventBridge.cs");

        Assert.Contains("events.Subscribe<DamageAppliedEvent>(OnDamageApplied)", source);
        Assert.Contains("events.Subscribe<ActorDiedEvent>(OnActorDied)", source);
        Assert.Contains("_damageSubscription.Dispose();", source);
        Assert.Contains("_deathSubscription.Dispose();", source);
        Assert.Contains("if (evt.Amount <= 0)", source);
    }

    [Fact]
    public void Effects_Never_Touch_System_Random_Or_The_Simulation_Random_Streams()
    {
        string directory = Path.Combine(RepoRoot(), "dream", "Rendering", "Effects");
        string scene = Path.Combine(RepoRoot(), "dream", "Client", "Scenes", "PlayScene.Effects.cs");
        string[] files = [.. Directory.GetFiles(directory, "*.cs"), scene];

        Assert.True(files.Length >= 9);
        foreach (string file in files)
        {
            string code = StripComments(File.ReadAllText(file));
            Assert.DoesNotMatch(@"\bRandom\b", code);
            Assert.DoesNotMatch(@"Random\.Shared", code);
            Assert.DoesNotContain("DeterministicRandom", code);
            Assert.DoesNotContain("Dream.Simulation.Determinism", code);
            Assert.DoesNotContain("ItemRoller", code);
            Assert.DoesNotContain("LootRoller", code);
        }
    }

    [Fact]
    public void The_Effects_Simulation_Has_No_Gl_Dependency()
    {
        foreach (string name in new[] { "FxSimulation.cs", "FxParticlePool.cs", "FxRandom.cs", "FxViewBasis.cs", "FxInstance.cs", "FxEventBridge.cs", "FxFlashList.cs", "FxShadowList.cs", "FxGroundProbe.cs" })
        {
            string code = ReadSource("dream/Rendering/Effects/" + name);
            Assert.DoesNotContain("OpenTK.Graphics", code);
            Assert.DoesNotContain("GL.", code);
        }
    }

    private static int IndexOf(string source, string value)
    {
        int index = source.IndexOf(value, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Missing: {value}");
        return index;
    }

    private static string Method(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
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
                return source[start..(i + 1)];
            }
        }

        throw new InvalidOperationException("Unbalanced braces.");
    }

    private static string StripComments(string source)
    {
        string withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(withoutBlocks, @"//[^\n]*", string.Empty);
    }

    private static string ReadShader(string fileName) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "dream", "shaders", fileName));

    private static string ReadSource(string relative) =>
        PartialClassSource.Read(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

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
