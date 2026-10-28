using System.Text.RegularExpressions;
using Xunit;
using Dream.Rendering.Core;
using Dream.Tests.Utilities;

namespace Dream.Tests.Rendering.World;

// Wave 12 tester, GPU-6 GL state (no GL context in tests, so structural checks on the source):
// - InstanceMatrixBuffer binds instanceModel's four columns (locations 6..9) with divisor 1;
// - the divisor is never reset, which is safe only because it is VAO state on VAOs whose other
//   users never read locations 6..9 for anything else: no VAO in the renderer enables or points an
//   attribute at 6..9 except InstanceMatrixBuffer, and no shader declares another input there;
// - a non-instanced draw that follows an instanced one on the same VAO still fetches instance 0
//   from the instance buffer, which is valid only if that buffer has storage: every BindAttributes
//   runs after an Upload of at least one matrix (the count == 0 early returns);
// - useInstancing is put back to false before any non-instanced draw: at the end of each instanced
//   path, and by every path that starts a non-instanced draw.
public class Wave12InstancedAttributeStateTests
{
    [Fact]
    public void InstanceAttributes_AreLocations6To9_WithDivisor1()
    {
        string buffer = Read("dream/Rendering/Core/InstanceMatrixBuffer.cs");
        string bind = Method(buffer, "public void BindAttributes(");
        Assert.Equal(6, InstanceMatrixBuffer.FirstAttributeLocation);
        Assert.Equal(4, InstanceMatrixBuffer.AttributeColumnCount);
        Assert.Contains("int attributeLocation = FirstAttributeLocation + column;", bind);
        Assert.Contains("GL.EnableVertexAttribArray(attributeLocation);", bind);
        Assert.Contains("GL.VertexAttribDivisor(attributeLocation, 1);", bind);
        Assert.Contains("column * 4 * sizeof(float)", bind);
        Assert.Contains("GL.BindBuffer(BufferTarget.ArrayBuffer, _handle);", bind);
    }

    // The leftover divisor on 6..9 cannot leak into another attribute.
    [Fact]
    public void NoOtherVertexSetupOrShaderInput_UsesLocations6To9()
    {
        string root = RepoRoot();
        Regex literalLocation = new(@"(EnableVertexAttribArray|VertexAttribPointer|VertexAttribIPointer|VertexAttribDivisor)\(\s*(\d+)\s*[,)]");
        List<string> offenders = [];
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "dream"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            foreach (Match match in literalLocation.Matches(File.ReadAllText(file)))
            {
                if (int.Parse(match.Groups[2].Value) >= InstanceMatrixBuffer.FirstAttributeLocation)
                {
                    offenders.Add($"{Path.GetFileName(file)}: {match.Value}");
                }
            }
        }

        Assert.Empty(offenders);

        Regex shaderLocation = new(@"layout\s*\(\s*location\s*=\s*(\d+)\s*\)\s*in\s+(\w+)\s+(\w+)");
        foreach (string shader in Directory.EnumerateFiles(Path.Combine(root, "dream", "shaders"), "*.glsl"))
        {
            foreach (Match match in shaderLocation.Matches(File.ReadAllText(shader)))
            {
                int location = int.Parse(match.Groups[1].Value);
                if (location >= InstanceMatrixBuffer.FirstAttributeLocation)
                {
                    Assert.True(match.Groups[3].Value == "instanceModel" && match.Groups[2].Value == "mat4", $"{Path.GetFileName(shader)}: {match.Value}");
                }
            }
        }
    }

    // BindAttributes on a buffer that was never uploaded would point 6..9 at buffer 0.
    [Fact]
    public void EveryInstancedDraw_UploadsAtLeastOneMatrixBeforeBindingAttributes()
    {
        string render = PartialClassSource.Read(Path.Combine(RepoRoot(), "dream", "Rendering", "Core", "Render.cs"));
        string fallback = Method(render, "public void DrawFallbackInstances(");
        int upload = fallback.IndexOf("_fallbackInstances.Upload(", StringComparison.Ordinal);
        Assert.True(upload >= 0);
        Assert.True(fallback.IndexOf("if (count == 0)", StringComparison.Ordinal) < upload);
        Assert.True(upload < fallback.IndexOf("DrawInstanced(", StringComparison.Ordinal));

        string model = Read("dream/Rendering/Models/AnimatedModel.cs");
        string prepared = Method(model[model.LastIndexOf("public void RenderPreparedInstanced(", StringComparison.Ordinal)..], "public void RenderPreparedInstanced(");
        Assert.Contains("if (count == 0)", prepared);
        string node = Method(model, "private void RenderNodeInstanced(");
        Assert.True(node.IndexOf("_instanceMatrixBuffer.Upload(", StringComparison.Ordinal) < node.IndexOf("_instanceMatrixBuffer.BindAttributes();", StringComparison.Ordinal));

        // The upload helper itself never creates a handle for an empty upload.
        string buffer = Method(Read("dream/Rendering/Core/InstanceMatrixBuffer.cs"), "public void Upload(");
        Assert.True(buffer.IndexOf("if (count == 0)", StringComparison.Ordinal) < buffer.IndexOf("GL.GenBuffer()", StringComparison.Ordinal));
    }

    // useInstancing back to false: after each instanced path, and at the start of every
    // non-instanced path that shares the program.
    [Fact]
    public void UseInstancing_IsResetAfterInstancedDraws_AndSetByEveryNonInstancedPath()
    {
        string render = PartialClassSource.Read(Path.Combine(RepoRoot(), "dream", "Rendering", "Core", "Render.cs"));
        string fallback = Method(render, "public void DrawFallbackInstances(");
        Assert.Matches(new Regex(@"GetShader\(""cube""\)\.SetBool\(""useInstancing"", false\);\s*\}$"), fallback);
        Assert.Contains("shader.SetBool(\"useInstancing\", false);", Method(render, "public void BeginStaticTerrainPass("));
        Assert.Contains("shader.SetBool(\"useInstancing\", instanced);", Method(render, "private void BindTerrainMaterial("));

        string model = Read("dream/Rendering/Models/AnimatedModel.cs");
        string prepared = Method(model[model.LastIndexOf("public void RenderPreparedInstanced(", StringComparison.Ordinal)..], "public void RenderPreparedInstanced(");
        Assert.Matches(new Regex(@"shader\.SetBool\(""useInstancing"", false\);\s*\}$"), prepared);
        // A mid-node switch to the alpha-mask variant sets the flag on that program every time,
        // since an instanced node may have left the variant with useInstancing = true.
        Assert.Contains("program.SetBool(\"useInstancing\", instanced);", Method(model, "private void BindProgram("));
        Assert.Contains("BindProgram(program, instanced: false, modelMatrix);", Method(model, "private void RenderNode("));
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
        File.ReadAllText(Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

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
