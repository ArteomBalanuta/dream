using System.Text.RegularExpressions;
using Dream.Rendering.Ground;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.Ground;

public class GroundWindTests
{
    private static readonly GroundAssetClass[] Plants =
    [
        GroundAssetClass.Tree, GroundAssetClass.Sapling, GroundAssetClass.Bush, GroundAssetClass.Grass,
        GroundAssetClass.Flower, GroundAssetClass.Mushroom, GroundAssetClass.Leaves, GroundAssetClass.Twigs,
    ];

    [Fact]
    public void Every_Non_Plant_Class_Has_Zero_Amplitude()
    {
        foreach (GroundAssetClass assetClass in Enum.GetValues<GroundAssetClass>())
        {
            if (Array.IndexOf(Plants, assetClass) >= 0)
            {
                Assert.True(GroundWind.For(assetClass).Amplitude > 0f, assetClass.ToString());
                continue;
            }

            Assert.Equal(0f, GroundWind.For(assetClass).Amplitude);
            Assert.Equal(Vector2.Zero, GroundWind.Uniform(assetClass, 2f));
        }
    }

    [Fact]
    public void The_Table_Matches_The_Authored_Amplitudes()
    {
        Assert.Equal(new GroundWind.Sway(0.10f, 2.5f), GroundWind.For(GroundAssetClass.Tree));
        Assert.Equal(new GroundWind.Sway(0.045f, 1.3f), GroundWind.For(GroundAssetClass.Grass));
        Assert.Equal(new GroundWind.Sway(0.006f, 2.0f), GroundWind.For(GroundAssetClass.Mushroom));
    }

    [Fact]
    public void A_Tree_Tip_Never_Moves_More_Than_Its_Amplitude_And_The_Trunk_Barely_At_All()
    {
        const float height = 5.5f;
        Vector2 wind = GroundWind.Uniform(GroundAssetClass.Tree, height);
        float tipPeak = 0f;
        float trunkPeak = 0f;
        for (float t = 0f; t < 600f; t += 0.05f)
        {
            tipPeak = MathF.Max(tipPeak, MathF.Abs(GroundWind.Displacement(wind, height, t, 1.3f)));
            trunkPeak = MathF.Max(trunkPeak, MathF.Abs(GroundWind.Displacement(wind, height * 0.2f, t, 1.3f)));
        }

        Assert.InRange(tipPeak, 0.06f, 0.10001f);
        Assert.True(trunkPeak < 0.005f, $"trunk at 20% height moved {trunkPeak}");
    }

    [Fact]
    public void A_Zero_Uniform_Displaces_Exactly_Nothing()
    {
        for (float y = 0f; y < 6f; y += 0.5f)
        {
            Assert.Equal(0f, GroundWind.Displacement(Vector2.Zero, y, 12.3f, 0.7f));
        }
    }

    [Fact]
    public void The_Vertex_Shader_Gates_The_Sway_On_The_Uniform_And_Reads_The_Frame_Clock()
    {
        string vertex = File.ReadAllText(Path.Combine(RepoRoot(), "dream", "shaders", "cubeVertex.glsl"));
        Assert.Contains("uniform vec2 uWind;", vertex);
        Assert.Contains("if (useInstancing && uWind.x > 0.0)", vertex);
        Assert.Contains("uFrameTime.x", vertex);
        Assert.Contains("instanceModel[3].xyz", vertex);
        Assert.Contains("pow(max(aPos.y, 0.0), uWind.y)", vertex);
    }

    [Fact]
    public void The_Wind_Uniform_Uploads_Changes_Only_And_Reset_Returns_To_Zero()
    {
        List<Vector2> sequence = [];
        WindUniform uniform = new(sequence.Add);

        uniform.Reset(); // already zero: nothing to send
        uniform.Set(new Vector2(0.01f, 2f));
        uniform.Set(new Vector2(0.01f, 2f)); // unchanged
        uniform.Set(new Vector2(0.02f, 1.3f));
        uniform.Reset();

        Assert.Equal([new Vector2(0.01f, 2f), new Vector2(0.02f, 1.3f), Vector2.Zero], sequence);
        Assert.Equal(Vector2.Zero, uniform.Current);
    }

    [Fact]
    public void The_Scatter_Renderer_Resets_Both_Programs_In_A_Finally_After_The_Draws()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "dream", "Rendering", "Ground", "GroundScatterRenderer.cs"));
        Match draw = Regex.Match(source, @"public void Draw\(.*?\n    \}\r?\n", RegexOptions.Singleline);
        Assert.True(draw.Success);
        int tryAt = draw.Value.IndexOf("try", StringComparison.Ordinal);
        int renderAt = draw.Value.IndexOf("RenderPreparedInstanced", StringComparison.Ordinal);
        int finallyAt = draw.Value.IndexOf("finally", StringComparison.Ordinal);
        Assert.InRange(tryAt, 0, renderAt - 1);
        Assert.True(finallyAt > renderAt);
        string afterFinally = draw.Value[finallyAt..];
        Assert.Contains("_terrainWind.Reset();", afterFinally);
        Assert.Contains("_actorWind.Reset();", afterFinally);
        // Both programs, the mask variants included, receive the uniform.
        Assert.Contains("AlphaMaskVariant", source);
        // Props never sway.
        string props = File.ReadAllText(Path.Combine(RepoRoot(), "dream", "Rendering", "Props", "PropRenderer.cs"));
        Assert.DoesNotContain("GroundWind", props);
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
