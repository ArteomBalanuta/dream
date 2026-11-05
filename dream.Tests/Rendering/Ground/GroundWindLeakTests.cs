using System.Text.RegularExpressions;
using Dream.Rendering.Ground;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Ground;

// cubeVertex.glsl is shared by the terrain, the world cubes and every actor, so a uWind left non-zero on
// one of those programs would sway things that must stand still. GL cannot run in a test, so the
// failure paths are pinned two ways: behaviourally on WindUniform (the tracker that decides whether a
// reset is sent) and structurally on the source of the places that write the uniform. GroundWindTests
// covers the happy-path sequence and the position of the finally.
public class GroundWindLeakTests
{
    // A GL call that fails part-way (the first program written, the mask variant not) must still leave
    // the tracker believing the uniform is non-zero, otherwise the reset in the finally is a no-op and
    // the half-written program keeps swaying.
    [Fact]
    public void A_Failed_Upload_Still_Lets_The_Reset_Send_Zero()
    {
        List<Vector2> sent = [];
        bool fail = true;
        WindUniform uniform = new(wind =>
        {
            sent.Add(wind);
            if (fail && wind != Vector2.Zero)
            {
                throw new InvalidOperationException("GL error after the first program was written");
            }
        });

        try
        {
            Assert.Throws<InvalidOperationException>(() => uniform.Set(new Vector2(0.02f, 1.3f)));
        }
        finally
        {
            uniform.Reset();
        }

        Assert.Equal([new Vector2(0.02f, 1.3f), Vector2.Zero], sent);
        Assert.Equal(Vector2.Zero, uniform.Current);
        fail = false;
    }

    [Fact]
    public void Switching_Between_Plants_And_A_Still_Asset_Returns_The_Uniform_To_Zero_In_Between()
    {
        List<Vector2> sent = [];
        WindUniform uniform = new(sent.Add);
        Vector2 tree = GroundWind.Uniform(GroundAssetClass.Tree, 5f);
        Vector2 grass = GroundWind.Uniform(GroundAssetClass.Grass, 0.5f);
        Assert.NotEqual(Vector2.Zero, tree);
        Assert.NotEqual(Vector2.Zero, grass);

        foreach (GroundAssetClass assetClass in Enum.GetValues<GroundAssetClass>())
        {
            uniform.Set(GroundWind.Uniform(assetClass, 2f));
        }

        uniform.Set(tree);
        uniform.Set(GroundWind.Uniform(GroundAssetClass.Tree, 0f)); // unusable height
        uniform.Set(GroundWind.Uniform(GroundAssetClass.Tree, float.NaN));
        Assert.Equal(Vector2.Zero, uniform.Current);
        uniform.Set(grass);
        uniform.Reset();
        Assert.Equal(Vector2.Zero, sent[^1]);
        Assert.Equal(Vector2.Zero, uniform.Current);
    }

    // ApplyWind must write the mask variant too: masked leaves draw with the variant, so a variant that
    // is not reset keeps swaying every alpha-masked actor after the scatter. (GroundWindTests only
    // checks that the text "AlphaMaskVariant" occurs somewhere in the file.)
    [Fact]
    public void ApplyWind_Writes_Uwind_To_The_Program_And_To_Its_Mask_Variant_And_Restores_The_Program()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "dream", "Rendering", "Ground", "GroundScatterRenderer.cs"));
        Match method = Regex.Match(source, @"private static void ApplyWind\(Shader shader, Vector2 wind\)\s*\{(?<body>.*?)\n    \}", RegexOptions.Singleline);
        Assert.True(method.Success, "ApplyWind not found");
        string body = method.Groups["body"].Value;

        int baseWrite = body.IndexOf("shader.SetVector2(WindUniformName, wind);", StringComparison.Ordinal);
        int variantUse = body.IndexOf("maskProgram.Use();", StringComparison.Ordinal);
        int variantWrite = body.IndexOf("maskProgram.SetVector2(WindUniformName, wind);", StringComparison.Ordinal);
        int restore = body.LastIndexOf("shader.Use();", StringComparison.Ordinal);
        Assert.True(baseWrite >= 0, "the base program is not written");
        Assert.True(variantUse > baseWrite, "the mask variant is not made current before it is written");
        Assert.True(variantWrite > variantUse, "the mask variant is not written");
        Assert.True(restore > variantWrite, "the base program is not made current again after the variant");
        Assert.Equal("uWind", GroundScatterRenderer.WindUniformName);
    }

    // Both wind trackers are reset, and the tracker for each program is the one the draw writes: the
    // terrain program for Decal/Cover layers, the actor program for the rest. A third tracker, or a
    // draw that bypasses them, would leave a program that no finally resets.
    [Fact]
    public void Every_Write_Of_Uwind_Goes_Through_A_Tracker_That_The_Finally_Resets()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "dream", "Rendering", "Ground", "GroundScatterRenderer.cs"));
        Assert.Equal(2, Regex.Matches(source, @"new WindUniform\(").Count);
        Assert.Equal(2, Regex.Matches(source, @"\.SetVector2\(WindUniformName, wind\)").Count);
        Assert.Equal(1, Regex.Matches(source, @"maskProgram\.SetVector2\(WindUniformName, wind\)").Count);
        Assert.Contains("(terrain ? _terrainWind : _actorWind).Set(", source);
    }

    // AnimatedModel writes the model's Wind to whichever program it switches to. Only the scatter sets a
    // non-zero Wind, so every other draw of any model writes zero and heals a program left non-zero.
    [Fact]
    public void Only_The_Scatter_Assigns_A_Wind_To_A_Model_And_Every_Program_Switch_Writes_It()
    {
        string root = RepoRoot();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "dream"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                file.EndsWith("GroundScatterRenderer.cs", StringComparison.Ordinal) ||
                file.EndsWith("AnimatedModel.cs", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.DoesNotMatch(@"\.Wind\s*=(?!=)", File.ReadAllText(file));
        }

        string model = File.ReadAllText(Path.Combine(root, "dream", "Rendering", "Models", "AnimatedModel.cs"));
        Match bind = Regex.Match(model, @"private void BindProgram\(.*?\n    \}", RegexOptions.Singleline);
        Assert.True(bind.Success);
        Assert.Contains("program.SetVector2(\"uWind\", Wind);", bind.Value);
        Assert.Contains("program.Use();", bind.Value);
    }

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
