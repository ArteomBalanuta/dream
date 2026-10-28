using System.Text.RegularExpressions;
using Dream.Tests.Utilities;
using Xunit;

namespace Dream.Tests.Rendering;

// GL is unavailable in tests, so these check the source structure of the dispose paths.
public class DisposeRobustnessStructuralTests
{
    [Fact]
    public void RenderDispose_AssertsRenderThreadBeforeSettingDisposed()
    {
        string body = Method(Read("dream/Rendering/Core/Render.cs"), "public void Dispose()");
        int assertAt = body.IndexOf("AssertRenderThread", StringComparison.Ordinal);
        int flagAt = body.IndexOf("_disposed = true", StringComparison.Ordinal);
        Assert.True(assertAt >= 0 && flagAt > assertAt);
    }

    [Fact]
    public void RenderDispose_ReleasesEachHudResourceInItsOwnStep()
    {
        string source = Read("dream/Rendering/Core/Render.cs");
        string body = Method(source, "public void Dispose()");
        Assert.DoesNotContain("DisposeHud", source);
        foreach (string step in new[]
        {
            "_fallbackCubeBuffer.Dispose", "_staticTerrainTextures.Dispose", "DeleteFontTexture",
            "_hudShader.Dispose", "_shaderObj.Dispose", "DeleteHudBuffers",
        })
        {
            Assert.Matches(new Regex(@"RunDisposeStep\(" + Regex.Escape(step) + @",\s*ref failures\)"), body);
        }
    }

    [Fact]
    public void RemotePlayerRendererDispose_DisposesModelsBeforeGuardedTrackerCleanup()
    {
        string body = Method(Read("dream/Rendering/Actors/RemotePlayerRenderer.cs"), "public void Dispose()");
        int modelsAt = body.IndexOf("model.Dispose()", StringComparison.Ordinal);
        int trackerAt = body.IndexOf("_clipTracker.RemoveExcept", StringComparison.Ordinal);
        Assert.True(modelsAt >= 0 && trackerAt > modelsAt);
        Assert.Matches(new Regex(@"try\s*\{\s*_clipTracker\.RemoveExcept"), body);
    }

    [Fact]
    public void AnimatedModel_AlwaysAppliesAMaterial_IncludingDefaultForMissingIndex()
    {
        string source = Read("dream/Rendering/Models/AnimatedModel.cs");
        // Each draw path (plain and instanced) resolves the primitive's material, falling back to
        // the default, and applies it to the program that draws the primitive.
        Assert.Equal(2, Regex.Matches(source, @"MaterialData material = ResolveMaterial\(primitive\.MaterialIndex\);").Count);
        Assert.Equal(2, Regex.Matches(source, @"SetMaterialProperties\(program, material\);").Count);
        Assert.DoesNotContain("if (TryGetMaterial(primitive.MaterialIndex", source);
        Assert.Contains("TryGetMaterial(materialIndex, out MaterialData? material) ? material : DefaultMaterial", source);
        Assert.Matches(new Regex(@"DefaultMaterial = new\(\)\s*\{[^}]*BaseColorFactor = Vector4\.One"), source);
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
