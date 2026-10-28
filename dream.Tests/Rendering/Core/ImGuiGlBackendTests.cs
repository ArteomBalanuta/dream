using System.Numerics;
using System.Text.RegularExpressions;
using Dream.Rendering.Core;
using Dream.Tests.Utilities;
using Xunit;

namespace Dream.Tests.Rendering.Core;

// GL is unavailable in tests. The clip-rect conversion is checked directly; the GL state of the
// two ImGui backends (in-game HUD and startup menu) is checked from their source.
public class ImGuiGlBackendTests
{
    public static TheoryData<string> ImGuiBackends => new()
    {
        "dream/Rendering/Core/Render.cs",
        "dream/Client/Startup/StartupMenuRenderer.cs"
    };

    private static readonly Vector2 Origin = Vector2.Zero;
    private static readonly Vector2 UnitScale = Vector2.One;

    [Fact]
    public void Scissor_FlipsTopLeftClipRectToBottomLeftOrigin()
    {
        bool drawable = ImGuiScissorRect.TryFromClipRect(
            new Vector4(10f, 20f, 110f, 70f), Origin, UnitScale, 600, out ImGuiScissorRect scissor);

        Assert.True(drawable);
        Assert.Equal(new ImGuiScissorRect(10, 530, 100, 50), scissor);
    }

    [Fact]
    public void Scissor_FullDisplayClipRect_CoversWholeFramebuffer()
    {
        bool drawable = ImGuiScissorRect.TryFromClipRect(
            new Vector4(0f, 0f, 800f, 600f), Origin, UnitScale, 600, out ImGuiScissorRect scissor);

        Assert.True(drawable);
        Assert.Equal(new ImGuiScissorRect(0, 0, 800, 600), scissor);
    }

    [Fact]
    public void Scissor_SubtractsDisplayPositionAndAppliesFramebufferScale()
    {
        bool drawable = ImGuiScissorRect.TryFromClipRect(
            new Vector4(110f, 70f, 160f, 120f),
            new Vector2(100f, 50f),
            new Vector2(2f, 2f),
            1200,
            out ImGuiScissorRect scissor);

        Assert.True(drawable);
        // min (10,20)*2 = (20,40), max (60,70)*2 = (120,140); y = 1200 - 140.
        Assert.Equal(new ImGuiScissorRect(20, 1060, 100, 100), scissor);
    }

    [Theory]
    [InlineData(50f, 20f, 50f, 70f)]
    [InlineData(10f, 70f, 110f, 70f)]
    [InlineData(110f, 20f, 10f, 70f)]
    [InlineData(10f, 70f, 110f, 20f)]
    public void Scissor_EmptyOrInvertedClipRect_IsNotDrawable(float minX, float minY, float maxX, float maxY)
    {
        bool drawable = ImGuiScissorRect.TryFromClipRect(
            new Vector4(minX, minY, maxX, maxY), Origin, UnitScale, 600, out ImGuiScissorRect scissor);

        Assert.False(drawable);
        Assert.Equal(default, scissor);
    }

    [Theory]
    [MemberData(nameof(ImGuiBackends))]
    public void RenderImGuiDrawData_EnablesScissorTestForThePassAndRestoresItAfter(string relativePath)
    {
        string body = Method(Read(relativePath), "private void RenderImGuiDrawData(ImDrawDataPtr drawData)");
        int enableAt = body.IndexOf("GL.Enable(EnableCap.ScissorTest)", StringComparison.Ordinal);
        int drawAt = body.IndexOf("GL.DrawElements(", StringComparison.Ordinal);
        int restoreAt = body.IndexOf("previousState.Restore()", StringComparison.Ordinal);

        Assert.True(enableAt >= 0, "ScissorTest is never enabled, so ImGui clip rects are ignored.");
        Assert.True(drawAt > enableAt, "ScissorTest must be enabled before drawing.");
        // The snapshot puts the caller's scissor flag and box back; a disable right before it is
        // redundant and would hide a snapshot that forgot the flag.
        Assert.True(restoreAt > drawAt, "ScissorTest must be restored after the ImGui pass.");
        Assert.DoesNotContain("GL.Disable(EnableCap.ScissorTest)", body);
        string restore = Method(Read("dream/Rendering/Core/Render.cs"), "public void Restore()");
        Assert.Contains("SetEnabled(EnableCap.ScissorTest, _scissorTest)", restore);
        Assert.Contains("GL.Scissor(_scissorX, _scissorY, _scissorWidth, _scissorHeight)", restore);
        Assert.Contains("ImGuiScissorRect.TryFromClipRect(", body);
    }

    [Theory]
    [MemberData(nameof(ImGuiBackends))]
    public void RenderImGuiDrawData_SelectsTextureUnit0BeforeBindingTextures(string relativePath)
    {
        string body = Method(Read(relativePath), "private void RenderImGuiDrawData(ImDrawDataPtr drawData)");
        int activeAt = body.IndexOf("GL.ActiveTexture(TextureUnit.Texture0)", StringComparison.Ordinal);
        int bindAt = body.IndexOf("GL.BindTexture(", StringComparison.Ordinal);

        Assert.True(activeAt >= 0, "The ImGui pass must select texture unit 0.");
        Assert.True(bindAt > activeAt, "Texture unit 0 must be active before ImGui textures are bound.");
    }

    [Theory]
    [MemberData(nameof(ImGuiBackends))]
    public void RenderImGuiDrawData_CapturesAndRestoresGlState(string relativePath)
    {
        string body = Method(Read(relativePath), "private void RenderImGuiDrawData(ImDrawDataPtr drawData)");
        int captureAt = body.IndexOf("ImGuiGlStateSnapshot.Capture()", StringComparison.Ordinal);
        int firstStateChangeAt = body.IndexOf("GL.Enable(EnableCap.Blend)", StringComparison.Ordinal);
        int restoreAt = body.IndexOf(".Restore()", StringComparison.Ordinal);
        int drawAt = body.IndexOf("GL.DrawElements(", StringComparison.Ordinal);

        Assert.True(captureAt >= 0 && firstStateChangeAt > captureAt, "State must be captured before the pass changes it.");
        Assert.True(restoreAt > drawAt, "State must be restored after the pass.");
    }

    [Fact]
    public void ImGuiGlStateSnapshot_RestoresEveryCapabilityThePassChanges()
    {
        string source = Read("dream/Rendering/Core/Render.cs");
        string restore = Method(source, "public void Restore()");
        foreach (string capability in new[] { "Blend", "CullFace", "DepthTest", "ScissorTest" })
        {
            Assert.Contains($"GL.IsEnabled(EnableCap.{capability})", source);
            Assert.Contains($"SetEnabled(EnableCap.{capability},", restore);
        }

        Assert.Contains("GL.BlendFuncSeparate(", restore);
        Assert.Contains("GL.UseProgram(_program)", restore);
        Assert.Contains("GL.BindVertexArray(_vertexArray)", restore);
        Assert.Contains("GL.ActiveTexture((TextureUnit)_activeTexture)", restore);
    }

    [Theory]
    [MemberData(nameof(ImGuiBackends))]
    public void RenderImGuiDrawData_DoesNotLookUpUniformsOrRespecifyAttributesPerFrame(string relativePath)
    {
        string source = Read(relativePath);
        string body = Method(source, "private void RenderImGuiDrawData(ImDrawDataPtr drawData)");
        string setup = Method(source, "private void CreateImGuiResources()");

        Assert.DoesNotContain("GetUniformLocation", body);
        Assert.DoesNotContain("VertexAttribPointer", body);
        Assert.DoesNotContain("EnableVertexAttribArray", body);
        Assert.Contains("_projMtxLocation = GL.GetUniformLocation(_shader, \"ProjMtx\")", setup);
        Assert.Contains("GL.UniformMatrix4(_projMtxLocation,", body);
        Assert.Equal(3, Regex.Matches(setup, @"GL\.VertexAttribPointer\(").Count);
    }

    [Theory]
    [MemberData(nameof(ImGuiBackends))]
    public void FontTexture_IsUploadedAsRgba(string relativePath)
    {
        string body = Method(Read(relativePath), "private void CreateFontTexture()");

        Assert.Contains("GetTexDataAsRGBA32", body);
        Assert.Contains("PixelFormat.Rgba", body);
        Assert.DoesNotContain("PixelFormat.Bgra", body);
        Assert.Contains("GL.ActiveTexture(TextureUnit.Texture0)", body);
    }

    [Fact]
    public void Render_HasNoObsoleteFaceEnumsOrDeadSolidCube()
    {
        string render = Read("dream/Rendering/Core/Render.cs");
        string animatedModel = Read("dream/Rendering/Models/AnimatedModel.cs");

        foreach (string source in new[] { render, animatedModel })
        {
            Assert.DoesNotContain("MaterialFace.", source);
            Assert.DoesNotContain("CullFaceMode.", source);
        }

        Assert.DoesNotContain("DrawSolidCube", render);
    }

    private static string Method(string source, string signature)
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

    private static string Read(string relative) =>
        PartialClassSource.Read(Path.Combine(FindRepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepoRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "dream")) &&
                Directory.Exists(Path.Combine(current.FullName, "dream.Tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
