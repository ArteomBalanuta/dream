using System.Text.RegularExpressions;
using Dream.Tests.Utilities;
using Xunit;

namespace Dream.Tests.Rendering.Core;

// GL is unavailable in tests, so these check the source. Collision wireframes use the "default"
// program; with alpha 0 and blending left on by the ImGui restore they drew nothing. The world
// pass now sets its own blend and depth state, and the default program is opaque.
public class WorldPassGlStateTests
{
    [Fact]
    public void DefaultFragmentShader_OutputsOpaqueAlpha()
    {
        string shader = Read("dream/shaders/fragmentShader.glsl");

        Assert.Matches(@"fragColor\s*=\s*vec4\(\s*color\s*,\s*1\.0\s*\)\s*;", shader);
        Assert.DoesNotMatch(@"fragColor\s*=\s*vec4\(\s*color\s*,\s*0(\.0)?\s*\)", shader);
    }

    [Fact]
    public void PlayScene_WorldPass_DisablesBlendAndEnablesDepthTestBeforeDrawingTheWorld()
    {
        string source = Read("dream/Client/Scenes/PlayScene.cs");
        string render = Method(source, "public void Render(FrameEventArgs args");
        int clearAt = render.IndexOf("GL.Clear(", StringComparison.Ordinal);
        int blendOffAt = render.IndexOf("GL.Disable(EnableCap.Blend);", StringComparison.Ordinal);
        int depthOnAt = render.IndexOf("GL.Enable(EnableCap.DepthTest);", StringComparison.Ordinal);
        int worldAt = render.IndexOf(".DrawWorld(", StringComparison.Ordinal);

        Assert.True(clearAt >= 0, "The world pass clears the framebuffer.");
        Assert.True(blendOffAt >= 0, "The world pass must turn blending off itself.");
        Assert.True(depthOnAt >= 0, "The world pass must turn depth testing on itself.");
        Assert.True(worldAt > blendOffAt && worldAt > depthOnAt, "World state is set before the world draws.");
        Assert.DoesNotContain("GL.Enable(EnableCap.Blend)", render[..worldAt]);
    }

    [Fact]
    public void Render_Constructor_SetsNoGlobalBlendState()
    {
        string source = Read("dream/Rendering/Core/Render.cs");
        string constructor = Method(source, "public Render(GameSettings settings)");

        Assert.DoesNotContain("EnableCap.Blend", constructor);
        Assert.DoesNotContain("GL.BlendFunc", constructor);
    }

    [Theory]
    [InlineData("dream/Rendering/Core/Render.cs")]
    [InlineData("dream/Client/Startup/StartupMenuRenderer.cs")]
    public void ImGuiPass_EnablesItsOwnBlending(string relativePath)
    {
        string body = Method(Read(relativePath), "private void RenderImGuiDrawData(ImDrawDataPtr drawData)");
        int blendAt = body.IndexOf("GL.Enable(EnableCap.Blend)", StringComparison.Ordinal);
        int drawAt = body.IndexOf("GL.DrawElements(", StringComparison.Ordinal);

        Assert.True(blendAt >= 0 && drawAt > blendAt, "The ImGui pass enables blending before it draws.");
    }

    [Fact]
    public void Render_HasNoByteOrderMarkInsideTheFile()
    {
        string source = Read("dream/Rendering/Core/Render.cs");
        Assert.DoesNotContain('﻿', source.TrimStart('﻿'));
        Assert.False(Regex.IsMatch(source, "\n﻿"));
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
