using System.Text.RegularExpressions;
using Dream.Rendering.Core;
using Dream.Tests.Integration;
using Dream.Tests.Support;
using Xunit;

namespace Dream.Tests.Rendering.Core;

// Compiles and links every GLSL program the product creates, in a hidden GL context, against the
// repo's dream/shaders. A shader that does not compile used to be found only by launching the game.
[Collection(RenderThreadAffinityCollection.Name)]
public sealed class ShaderCompileTests
{
    internal sealed record ShaderProgram(string Name, string Vertex, string Fragment, string? Define);

    // One row per `new Shader(...)` pair in the product. Every world program is also built with its
    // MASK variant (Shader.AlphaMaskDefine), the way Render.LoadWorldShader does.
    internal static readonly ShaderProgram[] Programs =
    {
        new("default", "vertexShader.glsl", "fragmentShader.glsl", null),
        new("cube", "cubeVertex.glsl", "cubeFragment.glsl", null),
        new("cube.mask", "cubeVertex.glsl", "cubeFragment.glsl", Shader.AlphaMaskDefine),
        new("terrain", "cubeVertex.glsl", "cubeFragment.glsl", Shader.TerrainDefine),
        new("enemy", "cubeVertex.glsl", "enemyFragment.glsl", null),
        new("enemy.mask", "cubeVertex.glsl", "enemyFragment.glsl", Shader.AlphaMaskDefine),
        new("shadowDepth", "cubeVertex.glsl", "depthFragment.glsl", null),
        new("shadowDepth.mask", "cubeVertex.glsl", "depthFragment.glsl", Shader.AlphaMaskDefine),
        new("hud", "hudVertex.glsl", "hudFragment.glsl", null),
        new("hudText", "hudVertexText.glsl", "hudFragmentText.glsl", null),
        new("fx", "fxVertex.glsl", "fxFragment.glsl", null),
        new("sky", "skyVertex.glsl", "skyFragment.glsl", null),
        new("boundaryVoid", "skyVertex.glsl", "boundaryVoidFragment.glsl", null),
        new("warMist", "warMistVertex.glsl", "warMistFragment.glsl", null),
        new("ui", "uiVertex.glsl", "uiFragment.glsl", null),
        new("post.bright", "postVertex.glsl", "postBrightFragment.glsl", null),
        new("post.blur", "postVertex.glsl", "postBlurFragment.glsl", null),
        new("post.resolve", "postVertex.glsl", "postResolveFragment.glsl", null),
    };

    private static readonly Regex IncludePattern = new("^[ \\t]*#[ \\t]*include[ \\t]*\"([^\"]+)\"", RegexOptions.Multiline);

    private static string ShaderDirectory => Path.Combine(RepoRoot.Find(), "dream", "shaders");

    [GlFact]
    public void Every_Product_Program_Compiles_And_Links()
    {
        string directory = ShaderDirectory;
        HiddenGlContext.Run(() =>
        {
            List<string> failures = new();
            foreach (ShaderProgram program in Programs)
            {
                try
                {
                    using Shader shader = new(
                        Path.Combine(directory, program.Vertex),
                        Path.Combine(directory, program.Fragment),
                        program.Define);
                    Assert.NotEqual(0, shader.Handle);
                }
                catch (Exception ex)
                {
                    failures.Add($"{program.Name}: {ex.Message}");
                }
            }

            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        });
    }

    // Synthetic negative: the gate above can still fail. A real shader with a syntax error injected
    // must make `new Shader` throw.
    [GlFact]
    public void A_Syntax_Error_Makes_Shader_Creation_Throw()
    {
        string directory = ShaderDirectory;
        string temp = Path.Combine(Path.GetTempPath(), "dream-shader-negative-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string source = File.ReadAllText(Path.Combine(directory, "fragmentShader.glsl"));
            int mainIndex = source.IndexOf("void main", StringComparison.Ordinal);
            Assert.True(mainIndex > 0, "fragmentShader.glsl has no main().");
            string broken = source.Insert(mainIndex, "this is not glsl;\n");
            string brokenPath = Path.Combine(temp, "fragmentShader.glsl");
            File.WriteAllText(brokenPath, broken);

            HiddenGlContext.Run(() =>
            {
                Exception ex = Assert.ThrowsAny<Exception>(() =>
                    new Shader(Path.Combine(directory, "vertexShader.glsl"), brokenPath).Dispose());
                Assert.Contains("compilation failed", ex.Message);
            });
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void Every_Shader_File_Is_Covered_By_The_Program_Table()
    {
        Assert.Empty(FindCoverageProblems(ShaderDirectory, Programs));
    }

    // Synthetic negative: an orphan `#version` file nothing lists, and an include nobody reaches.
    [Fact]
    public void Coverage_Helper_Flags_An_Orphan_Version_File_And_An_Unreached_Include()
    {
        string temp = Path.Combine(Path.GetTempPath(), "dream-shader-orphan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            File.WriteAllText(Path.Combine(temp, "a.vert"), "#version 330 core\nvoid main() {}\n");
            File.WriteAllText(Path.Combine(temp, "a.frag"), "#version 330 core\n#include \"shared.glsl\"\nvoid main() {}\n");
            File.WriteAllText(Path.Combine(temp, "shared.glsl"), "// shared\n");
            ShaderProgram[] table = { new("a", "a.vert", "a.frag", null) };
            Assert.Empty(FindCoverageProblems(temp, table));

            File.WriteAllText(Path.Combine(temp, "orphan.frag"), "\uFEFF#version 330 core\nvoid main() {}\n");
            File.WriteAllText(Path.Combine(temp, "lonely.glsl"), "// nobody includes me\n");
            List<string> problems = FindCoverageProblems(temp, table);
            Assert.Contains(problems, p => p.Contains("orphan.frag"));
            Assert.Contains(problems, p => p.Contains("lonely.glsl"));
            Assert.Equal(2, problems.Count);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void Table_Lists_Every_World_Program_With_Its_Mask_Variant()
    {
        foreach (string world in new[] { "cube", "enemy", "shadowDepth" })
        {
            Assert.Contains(Programs, p => p.Name == world && p.Define == null);
            Assert.Contains(Programs, p => p.Name == world + ".mask" && p.Define == Shader.AlphaMaskDefine);
        }

        Assert.Contains(Programs, p => p.Name == "terrain" && p.Define == Shader.TerrainDefine);
    }

    // Every *.glsl whose first non-BOM line is #version must be a table file; every other file must be
    // reached by an #include from a table file (one level, as Shader.ResolveIncludes allows).
    internal static List<string> FindCoverageProblems(string directory, IEnumerable<ShaderProgram> programs)
    {
        List<string> problems = new();
        HashSet<string> listed = new(StringComparer.OrdinalIgnoreCase);
        foreach (ShaderProgram program in programs)
        {
            listed.Add(program.Vertex);
            listed.Add(program.Fragment);
        }

        HashSet<string> included = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in listed)
        {
            string path = Path.Combine(directory, name);
            if (!File.Exists(path))
            {
                problems.Add($"{name}: listed in the program table but missing from {directory}");
                continue;
            }

            foreach (Match match in IncludePattern.Matches(File.ReadAllText(path)))
            {
                included.Add(match.Groups[1].Value);
            }
        }

        foreach (string file in Directory.GetFiles(directory).Select(Path.GetFileName).OfType<string>().OrderBy(n => n, StringComparer.Ordinal))
        {
            string first = File.ReadLines(Path.Combine(directory, file)).FirstOrDefault()?.TrimStart('\uFEFF').TrimStart() ?? "";
            if (first.StartsWith("#version", StringComparison.Ordinal))
            {
                if (!listed.Contains(file))
                {
                    problems.Add($"{file}: starts with #version but is in no program of the table");
                }
            }
            else if (!included.Contains(file))
            {
                problems.Add($"{file}: has no #version and no table file #includes it");
            }
        }

        return problems;
    }
}
