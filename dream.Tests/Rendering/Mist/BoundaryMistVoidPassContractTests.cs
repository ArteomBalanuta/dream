using System.IO;
using System.Text.RegularExpressions;
using Dream.Rendering.Mist;
using Dream.Tests.Utilities;
using Xunit;

namespace Dream.Tests.Rendering.Mist;

// GL is unavailable in tests, so the void pass is checked through its sources: the shader contract,
// the GL state the renderer sets and restores, and the frame order and lifetime in PlayScene.
public class BoundaryMistVoidPassContractTests
{
    private static readonly Regex UniformDeclaration = new(@"^\s*uniform\s+\w+\s+(\w+)\s*;", RegexOptions.Multiline);

    [Fact]
    public void Every_Declared_Void_Uniform_Is_Set_By_The_Renderer()
    {
        HashSet<string> declared = new(StringComparer.Ordinal);
        foreach (string file in new[] { BoundaryMistVoidRenderer.VertexShaderFile, BoundaryMistVoidRenderer.FragmentShaderFile })
        {
            foreach (Match match in UniformDeclaration.Matches(StripComments(ReadShader(file))))
            {
                declared.Add(match.Groups[1].Value);
            }
        }

        // The mist and lighting includes declare blocks, not loose uniforms, so only the sky vertex one remains
        // (the true eye comes from the BoundaryMist block, so the wall fragments and the void share one ray).
        Assert.Equal(declared.OrderBy(name => name), BoundaryMistVoidRenderer.UniformNames.OrderBy(name => name));

        string draw = Method(ReadSource("dream/Rendering/Mist/BoundaryMistVoidRenderer.cs"), "public void Draw(");
        Assert.DoesNotContain("GetUniformLocation", draw);
        // No per-frame allocation (the only `new` allowed is the exception thrown after disposal).
        Assert.DoesNotMatch(@"\bnew\s+(?!ObjectDisposedException)", draw);
        Assert.DoesNotContain("stackalloc", draw);
        Assert.Matches(@"_locations\[LocationInverseViewProjection\]", draw);
    }

    [Fact]
    public void The_Void_Shader_Reuses_The_Sky_Vertex_Shader_And_Shares_The_Smoke_Function()
    {
        Assert.Equal("skyVertex.glsl", BoundaryMistVoidRenderer.VertexShaderFile);

        string fragment = StripComments(ReadShader(BoundaryMistVoidRenderer.FragmentShaderFile));
        Assert.Matches(@"#include ""lighting\.glsl""\s*#include ""boundaryMist\.glsl""", fragment);
        Assert.Contains("FragColor = vec4(dreamBoundarySmoke(hit), 1.0);", fragment);
        // The hit is cut with the floor plane from the true camera; a hit inside the map is left alone.
        Assert.Contains("float t = (uMistParams.z - uMistEye.y) / dir.y;", fragment);
        Assert.Contains("dreamMistInset(hit) > 0.0", fragment);
        // Not a depth read.
        Assert.DoesNotContain("gl_FragCoord", fragment);
        Assert.DoesNotMatch(@"\bsampler\w*\b", fragment);
    }

    [Fact]
    public void The_Renderer_Draws_Behind_Everything_And_Restores_The_World_State()
    {
        string draw = Method(ReadSource("dream/Rendering/Mist/BoundaryMistVoidRenderer.cs"), "public void Draw(");

        Assert.Contains("GL.DepthFunc(DepthFunction.Lequal);", draw);
        Assert.Contains("GL.DepthMask(false);", draw);
        Assert.Contains("GL.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);", draw);
        int restore = draw.IndexOf("GL.Disable(EnableCap.Blend);", StringComparison.Ordinal);
        Assert.True(restore > draw.IndexOf("GL.DrawArrays", StringComparison.Ordinal));
        Assert.True(draw.IndexOf("GL.DepthMask(true);", restore, StringComparison.Ordinal) > restore);
        Assert.True(draw.IndexOf("GL.DepthFunc(DepthFunction.Less);", restore, StringComparison.Ordinal) > restore);
        Assert.Single(Regex.Matches(draw, @"GL\.DrawArrays\("));
    }

    [Fact]
    public void The_Scene_Uploads_Then_Draws_The_Void_Right_After_The_Sky_Branch()
    {
        string render = ReadSource("dream/Client/Scenes/PlayScene.Render.cs");

        int input = IndexOf(render, "_render.BoundaryMist = new BoundaryMistInput(");
        int world = IndexOf(render, "_render.World.DrawWorld(presentation);");
        int sky = IndexOf(render, "skyRenderer.Draw(in sky, presentation.Camera.GetViewMatrix(), _render.ProjectionMatrix);");
        int voidPass = IndexOf(render, "_boundaryMist?.Draw(");
        int effects = IndexOf(render, "DrawEffectsPass(in sky, in presentation, gpuTimer, (float)args.Time);");

        Assert.True(input < world, "the input is set before DrawWorld uploads the frame");
        Assert.True(sky < voidPass && voidPass < effects, "sky, then the void pass, then the effects pass");
        // Every camera mode: the call is not inside the non-isometric sky branch.
        Assert.Matches(@"if \(_boundaryMistSettings\.Enabled\)\s*\{\s*_boundaryMist\?\.Draw\(presentation\.Camera\.GetViewMatrix\(\), _render\.ProjectionMatrix\);\s*\}", render);
        Assert.Matches(@"(?m)^ {8}DrawEffectsPass\(in sky, in presentation, gpuTimer, \(float\)args\.Time\);\r?$", render);
    }

    [Fact]
    public void The_Void_Renderer_Is_Created_Guarded_And_Disposed_After_The_Sky()
    {
        string lifetime = ReadSource("dream/Client/Scenes/PlayScene.Lifetime.cs");

        // Created after the effects try/catch, in its own guarded method, so a fault logs once and costs only the void pass.
        int fxFault = IndexOf(lifetime, "RenderFaultLog.Shared.Report(\"effects\", exception);");
        Assert.True(fxFault < IndexOf(lifetime, "        CreateBoundaryMist();"));
        Assert.Matches(@"private void CreateBoundaryMist\(\)\s*\{\s*try\s*\{\s*_boundaryMist = new BoundaryMistVoidRenderer\(\);\s*\}\s*catch \(Exception exception\)\s*\{\s*_boundaryMist = null;\s*RenderFaultLog\.Shared\.Report\(""boundary-mist"", exception\);", lifetime);
        int sky = IndexOf(lifetime, "RunDisposeStep(DisposeSky, ref failures);");
        int mist = IndexOf(lifetime, "RunDisposeStep(DisposeBoundaryMist, ref failures);");
        Assert.True(sky < mist);
        Assert.Matches(@"private void DisposeBoundaryMist\(\)\s*\{\s*_boundaryMist\?\.Dispose\(\);\s*_boundaryMist = null;\s*\}", lifetime);
    }

    private static int IndexOf(string source, string value)
    {
        int index = source.IndexOf(value, StringComparison.Ordinal);
        Assert.True(index >= 0, $"Missing: {value}");
        return index;
    }

    private static string Method(string source, string signature)
    {
        int start = IndexOf(source, signature);
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
                return source.Substring(start, i - start + 1);
            }
        }

        throw new InvalidOperationException($"Unbalanced braces after {signature}");
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
