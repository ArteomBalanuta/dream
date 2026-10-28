using System.IO;
using System.Text.RegularExpressions;
using Dream.Rendering.Sky;
using Dream.Simulation.Players;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Sky;

// GL is unavailable in tests, so the sky pass is checked through its CPU math and its sources:
// the shader contract, the frame order in PlayScene.Render and the GL state the pass restores.
public class SkyPassContractTests
{
    private static readonly Regex UniformDeclaration = new(@"\buniform\s+\w+\s+(\w+)\s*;");

    [Fact]
    public void Sky_Vertex_Shader_Puts_Every_Vertex_At_Depth_One()
    {
        string vertex = StripComments(ReadShader(SkyRenderer.VertexShaderFile));

        // z == w, so the perspective divide gives NDC depth 1 (the far plane).
        Assert.Matches(@"gl_Position\s*=\s*vec4\(\s*ndc\s*,\s*1\.0\s*,\s*1\.0\s*\)\s*;", vertex);
        Assert.Contains("gl_VertexID", vertex);
        // No vertex attributes: the triangle comes from gl_VertexID.
        Assert.DoesNotMatch(@"\blayout\s*\(\s*location", vertex);
    }

    [Theory]
    [InlineData(SkyRenderer.VertexShaderFile)]
    [InlineData(SkyRenderer.FragmentShaderFile)]
    public void Sky_Shaders_Invert_Nothing_And_Sample_No_Texture(string fileName)
    {
        string shader = StripComments(ReadShader(fileName));

        Assert.StartsWith("#version 330 core", shader.TrimStart('﻿'));
        Assert.DoesNotMatch(@"\binverse\s*\(", shader);
        Assert.DoesNotMatch(@"\bsampler\w*\b", shader);
        Assert.DoesNotMatch(@"\btexture\s*\(", shader);
    }

    [Fact]
    public void Every_Declared_Sky_Uniform_Is_Set_By_The_Renderer()
    {
        HashSet<string> declared = new(StringComparer.Ordinal);
        foreach (string file in new[] { SkyRenderer.VertexShaderFile, SkyRenderer.FragmentShaderFile })
        {
            foreach (Match match in UniformDeclaration.Matches(StripComments(ReadShader(file))))
            {
                declared.Add(match.Groups[1].Value);
            }
        }

        Assert.Equal(declared.OrderBy(name => name), SkyRenderer.UniformNames.OrderBy(name => name));

        // Each name in the table is uploaded once per draw through its cached location.
        string renderer = ReadSource("dream/Rendering/Sky/SkyRenderer.cs");
        string draw = Method(renderer, "public void Draw(in SkyState sky");
        Assert.DoesNotContain("GetUniformLocation", draw);
        Assert.Equal(SkyRenderer.UniformNames.Length, Regex.Matches(draw, @"_locations\[Location\w+\]").Count);
        Assert.Contains("_shader.GetUniformLocations(UniformNames)", renderer);
    }

    [Fact]
    public void Sky_Is_Exactly_The_Horizon_Colour_At_And_Below_The_Horizon()
    {
        string fragment = StripComments(ReadShader(SkyRenderer.FragmentShaderFile));

        // The gradient starts at the horizon colour at elevation 0, and every added term (stars,
        // moon, sun, glow) is scaled by a fade that is 0 at and below the horizon.
        Assert.Matches(@"float elevation = clamp\(dir\.y, 0\.0, 1\.0\);", fragment);
        Assert.Matches(@"float above = smoothstep\(0\.0, HorizonFade, dir\.y\);", fragment);
        Assert.Matches(@"vec3 color = mix\(uHorizonColor, uZenithColor, sqrt\(elevation\)\);", fragment);
        foreach (Match addition in Regex.Matches(fragment, @"color (\+)?= (?!mix\(uHorizonColor)[^;]+;"))
        {
            Assert.Contains("above", addition.Value);
        }
    }

    [Fact]
    public void Inverse_View_Projection_Maps_The_Screen_Centre_To_The_Camera_Front_Wherever_The_Camera_Is()
    {
        Matrix4 projection = Projection();
        Vector3 facing = Vector3.Normalize(new Vector3(0.4f, 0.3f, -0.8f));
        Vector3? first = null;
        foreach (Vector3 position in new[] { Vector3.Zero, new Vector3(10f, 5f, -3f), new Vector3(-400f, 90f, 1200f) })
        {
            Camera camera = new(position);
            camera.SetFacing(facing);

            Matrix4 inverse = SkyRenderer.ComputeInverseViewProjection(camera.GetViewMatrix(), projection);
            Vector3 ray = Ray(inverse, Vector2.Zero);

            AssertClose(camera.Front, ray);
            first ??= ray;
            AssertClose(first.Value, ray);
        }
    }

    [Fact]
    public void Inverse_View_Projection_Recovers_The_Direction_Of_Any_Visible_Point()
    {
        Matrix4 projection = Projection();
        Camera camera = new(new Vector3(3f, 2f, 7f));
        camera.SetFacing(new Vector3(-0.2f, -0.1f, -1f));
        Matrix4 view = camera.GetViewMatrix();
        Matrix4 inverse = SkyRenderer.ComputeInverseViewProjection(view, projection);

        foreach (Vector3 offset in new[]
        {
            new Vector3(-0.5f, 0.2f, -1f), new Vector3(0.3f, -0.25f, -1f), new Vector3(0.6f, 0.3f, -0.9f),
        })
        {
            Vector3 direction = Vector3.Normalize(offset * new Matrix3(view).Inverted());
            Vector4 clip = new Vector4(camera.Position + direction * 40f, 1f) * view * projection;
            Vector2 ndc = clip.Xy / clip.W;
            Assert.InRange(ndc.X, -1f, 1f);
            Assert.InRange(ndc.Y, -1f, 1f);

            AssertClose(direction, Ray(inverse, ndc));
        }
    }

    [Fact]
    public void Inverse_View_Projection_Allocates_Nothing()
    {
        Matrix4 view = Matrix4.LookAt(new Vector3(1f, 2f, 3f), new Vector3(4f, 2f, 0f), Vector3.UnitY);
        Matrix4 projection = Projection();
        float sink = 0f;

        long bytes = AllocationAssert.MeasureBytesPerIteration(() =>
        {
            sink += SkyRenderer.ComputeInverseViewProjection(view, projection).M11;
        });

        Assert.Equal(0, bytes);
        GC.KeepAlive(sink);
    }

    [Fact]
    public void PlayScene_Evaluates_The_Sky_And_Sets_Lighting_And_Clear_Colour_Before_The_World()
    {
        string render = Method(ReadSource("dream/Client/Scenes/PlayScene.cs"), "public void Render(FrameEventArgs args");

        int evaluateAt = IndexOf(render, "SkyModel.Evaluate(_render.TimeOfDayOverride.Resolve(WorldTimeOfDay01), _skySettings)");
        int lightingAt = IndexOf(render, "_render.SetFrameLighting(in sky.Lighting);");
        int clearColorAt = IndexOf(render, "GL.ClearColor(sky.HorizonColor.X, sky.HorizonColor.Y, sky.HorizonColor.Z, 1f);");
        int clearAt = IndexOf(render, "GL.Clear(");
        int worldAt = IndexOf(render, ".DrawWorld(");

        Assert.True(evaluateAt < lightingAt && lightingAt < clearColorAt && clearColorAt < clearAt && clearAt < worldAt);
    }

    [Fact]
    public void PlayScene_Draws_The_Sky_After_The_Actors_And_Before_Debug_And_Viewmodel_In_The_Actors_Section()
    {
        string render = Method(ReadSource("dream/Client/Scenes/PlayScene.cs"), "public void Render(FrameEventArgs args");

        int actorsSectionAt = IndexOf(render, "gpuTimer.BeginSection(GpuTimerSection.Actors);");
        int enemiesAt = IndexOf(render, "_render.Enemies.DrawAtRenderTick(");
        int localPlayerAt = IndexOf(render, "_render.LocalPlayers.Draw(");
        int remotePlayersAt = IndexOf(render, "_render.RemotePlayers.Draw(");
        int skyAt = IndexOf(render, "skyRenderer.Draw(in sky, presentation.Camera.GetViewMatrix(), _render.ProjectionMatrix);");
        int debugAt = IndexOf(render, "_render.Debug.DrawCollisionBodies(");
        int viewModelAt = IndexOf(render, "_render.ViewModels.Draw(");
        int hudSectionAt = IndexOf(render, "gpuTimer.BeginSection(GpuTimerSection.Hud);");

        Assert.True(actorsSectionAt < enemiesAt && enemiesAt < remotePlayersAt && localPlayerAt < remotePlayersAt);
        Assert.True(remotePlayersAt < skyAt, "The sky draws after every opaque actor.");
        Assert.True(skyAt < debugAt && skyAt < viewModelAt, "Debug wireframes and the viewmodel draw over the sky.");
        Assert.True(skyAt < hudSectionAt, "The sky stays inside the Actors GPU timer section.");
        Assert.Single(Regex.Matches(render, @"\.Draw\(in sky,"));
    }

    [Fact]
    public void Sky_Pass_Sets_Its_State_And_Restores_The_World_State()
    {
        string draw = Method(ReadSource("dream/Rendering/Sky/SkyRenderer.cs"), "public void Draw(in SkyState sky");

        int lequalAt = IndexOf(draw, "GL.DepthFunc(DepthFunction.Lequal);");
        int writesOffAt = IndexOf(draw, "GL.DepthMask(false);");
        int blendOffAt = IndexOf(draw, "GL.Disable(EnableCap.Blend);");
        int cullOffAt = IndexOf(draw, "GL.Disable(EnableCap.CullFace);");
        int drawAt = IndexOf(draw, "GL.DrawArrays(PrimitiveType.Triangles, 0, 3);");
        int writesOnAt = IndexOf(draw, "GL.DepthMask(true);");
        int lessAt = IndexOf(draw, "GL.DepthFunc(DepthFunction.Less);");
        int cullOnAt = IndexOf(draw, "GL.Enable(EnableCap.CullFace);");

        Assert.True(lequalAt < drawAt && writesOffAt < drawAt && blendOffAt < drawAt && cullOffAt < drawAt);
        Assert.True(drawAt < writesOnAt && drawAt < lessAt && drawAt < cullOnAt);
        Assert.Contains("GL.IsEnabled(EnableCap.CullFace)", draw[..cullOffAt]);
        Assert.DoesNotContain("GL.Enable(EnableCap.Blend)", draw);
        Assert.Contains("RenderCounters.CountDraw();", draw);
        Assert.DoesNotContain("new ", Regex.Replace(draw, @"new Vector4\(|new ObjectDisposedException\(", string.Empty));
    }

    [Fact]
    public void PlayScene_Disposes_The_Sky_In_Its_Own_Step()
    {
        string source = ReadSource("dream/Client/Scenes/PlayScene.cs");
        string dispose = Method(source, "public void Dispose()");
        string disposeSky = Method(source, "private void DisposeSky()");

        Assert.Matches(@"RunDisposeStep\(DisposeSky,\s*ref failures\)", dispose);
        Assert.Contains("_sky?.Dispose();", disposeSky);
        Assert.Contains("_sky = null;", disposeSky);

        string rendererDispose = Method(ReadSource("dream/Rendering/Sky/SkyRenderer.cs"), "public void Dispose()");
        Assert.Contains("AssertRenderThread", rendererDispose);
        Assert.Contains("_shader?.Dispose();", rendererDispose);
        Assert.Contains("GL.DeleteVertexArray(_vertexArray);", rendererDispose);
    }

    [Fact]
    public void Time_Of_Day_Override_Stays_On_This_Client()
    {
        string hud = ReadSource("dream/Rendering/Core/Render.HudPanels.cs");
        string controls = Method(hud, "private static void DrawTimeOfDayControls(TimeOfDayOverride timeOfDay)");

        Assert.Contains("ImGui.CollapsingHeader(\"Time Of Day (debug, this client only)\")", controls);
        Assert.DoesNotContain("DefaultOpen", controls);
        Assert.DoesNotContain("Request", controls);
        Assert.Contains("DrawTimeOfDayControls(TimeOfDayOverride);", Method(hud, "private HudInteractionRequest DrawHudInterface("));

        // Nothing outside the renderer and the scene's render path knows about the override.
        string dream = Path.Combine(RepoRoot(), "dream");
        string[] users = Directory.EnumerateFiles(dream, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                           !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(path => File.ReadAllText(path).Contains("TimeOfDayOverride", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(dream, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[]
            {
                "Client/Scenes/PlayScene.Render.cs",
                "Rendering/Core/Render.HudPanels.cs",
                "Rendering/Core/Render.Sky.cs",
                "Rendering/Sky/TimeOfDayOverride.cs",
            },
            users);
    }

    private static Matrix4 Projection() =>
        Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(70f), 16f / 9f, 0.1f, 100f);

    // What the sky shaders compute: the far-plane point under an NDC position, divided per fragment.
    private static Vector3 Ray(Matrix4 inverseViewProjection, Vector2 ndc)
    {
        Vector4 far = new Vector4(ndc.X, ndc.Y, 1f, 1f) * inverseViewProjection;
        return Vector3.Normalize(far.Xyz / far.W);
    }

    private static void AssertClose(Vector3 expected, Vector3 actual)
    {
        Assert.True((expected - actual).Length < 1e-3f, $"Expected {expected}, got {actual}.");
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
