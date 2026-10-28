using Xunit;

namespace Dream.Tests.Rendering.Core;

// Wave 7 tester (R-HYG): GL is unavailable in tests and ImGuiGlStateSnapshot calls GL directly (no
// seam for a fake recorder), so its coverage is checked from source: every piece of GL state the
// ImGui pass sets must be captured before it and restored after it. Otherwise a prior state with,
// say, scissor on keeps the flag but not its box, and the next world pass is clipped to the last
// ImGui clip rect.
public class Wave7ImGuiGlStateCoverageTests
{
    [Fact]
    public void Snapshot_CapturesEveryCapabilityAndBindingItRestores()
    {
        string source = Read("dream/Rendering/Core/Render.cs");
        string capture = Method(source, "public static ImGuiGlStateSnapshot Capture()");
        string restore = Method(source, "public void Restore()");

        (string Captured, string Restored)[] pairs =
        [
            ("GL.IsEnabled(EnableCap.Blend)", "SetEnabled(EnableCap.Blend,"),
            ("GL.IsEnabled(EnableCap.CullFace)", "SetEnabled(EnableCap.CullFace,"),
            ("GL.IsEnabled(EnableCap.DepthTest)", "SetEnabled(EnableCap.DepthTest,"),
            ("GL.IsEnabled(EnableCap.ScissorTest)", "SetEnabled(EnableCap.ScissorTest,"),
            ("GetPName.BlendSrcRgb", "GL.BlendFuncSeparate("),
            ("GetPName.CurrentProgram", "GL.UseProgram(_program)"),
            ("GetPName.VertexArrayBinding", "GL.BindVertexArray(_vertexArray)"),
            ("GetPName.TextureBinding2D", "GL.BindTexture(TextureTarget.Texture2D, _texture2DOnUnit0)"),
            ("GetPName.ActiveTexture", "GL.ActiveTexture((TextureUnit)_activeTexture)")
        ];
        foreach ((string captured, string restored) in pairs)
        {
            Assert.Contains(captured, capture);
            Assert.Contains(restored, restore);
        }

        // Unit 0's binding is read after selecting unit 0, and the caller's unit is re-selected last.
        Assert.True(
            capture.IndexOf("GL.ActiveTexture(TextureUnit.Texture0)", StringComparison.Ordinal) <
            capture.IndexOf("GetPName.TextureBinding2D", StringComparison.Ordinal));
        Assert.True(
            restore.LastIndexOf("GL.ActiveTexture((TextureUnit)_activeTexture)", StringComparison.Ordinal) >
            restore.IndexOf("GL.BindTexture(", StringComparison.Ordinal));
    }

    // The pass calls GL.Scissor(...) and binds GL_ARRAY_BUFFER to its own vertex buffer, but the
    // snapshot keeps neither the scissor box nor the array-buffer binding. No world pass enables
    // scissor today, so this is latent.
    [Fact(Skip = "bug (latent, low): ImGuiGlStateSnapshot restores GL_SCISSOR_TEST but not GL_SCISSOR_BOX or GL_ARRAY_BUFFER_BINDING (wave 7 tester report)")]
    public void Snapshot_CapturesTheScissorBoxAndArrayBuffer_ThePassChanges()
    {
        string source = Read("dream/Rendering/Core/Render.cs");
        string pass = Method(source, "private void RenderImGuiDrawData(ImDrawDataPtr drawData)");
        string capture = Method(source, "public static ImGuiGlStateSnapshot Capture()");
        string restore = Method(source, "public void Restore()");

        Assert.Contains("GL.Scissor(", pass);
        Assert.Contains("GetPName.ScissorBox", capture);
        Assert.Contains("GL.Scissor(", restore);

        Assert.Contains("GL.BindBuffer(BufferTarget.ArrayBuffer", pass);
        Assert.Contains("GetPName.ArrayBufferBinding", capture);
        Assert.Contains("GL.BindBuffer(BufferTarget.ArrayBuffer", restore);
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

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

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
