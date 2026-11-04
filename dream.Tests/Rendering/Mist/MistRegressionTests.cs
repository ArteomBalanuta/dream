using System.IO;
using System.Text.RegularExpressions;
using Dream.Rendering.Mist;
using Xunit;

namespace Dream.Tests.Rendering.Mist;

// Gaps found while testing integrate/mist: world transfer resets, the runtime toggle, the wall's
// edge on all four sides, and the war mist's exact clear radius.
public class MistRegressionTests
{
    [Fact]
    public void A_Transfer_To_A_Map_Of_The_Same_Area_But_Another_Shape_Forgets_Everything_And_Takes_The_New_Size()
    {
        ExplorationMemory memory = new(30, 40);
        memory.Reveal(15f, 20f);
        memory.RevealAll();
        memory.ClearDirty();

        memory.Reset(40, 30); // same cell count, reused buffer

        Assert.Equal(40, memory.Width);
        Assert.Equal(30, memory.Depth);
        Assert.All(memory.Cells, cell => Assert.Equal(0, cell));
        Assert.True(memory.Dirty);
        Assert.True(memory.Reveal(15f, 15f));
        Assert.Equal(255, memory.Cells[15 * 40 + 15]);
        Assert.Equal(0, memory.Cells[29 * 40 + 39]);
    }

    [Fact]
    public void A_Transfer_To_A_Smaller_Map_Keeps_Reveals_In_Bounds()
    {
        ExplorationMemory memory = new(100, 100);
        memory.Reveal(95f, 95f);
        memory.Reset(20, 20);
        memory.Reveal(19.5f, 19.5f);

        Assert.Equal(400, memory.Cells.Length);
        Assert.Equal(255, memory.Cells[19 * 20 + 19]);
    }

    [Fact]
    public void The_Scene_Resets_The_Memory_When_The_Map_Instance_Changes_Before_Revealing()
    {
        string pass = Method(ReadWarMistSource(), "private void DrawWarMistPass(");

        Assert.Contains("!ReferenceEquals(map, _explorationMap)", pass);
        Assert.Contains("_exploration.Reset(map.Width, map.Depth);", pass);
        int reset = pass.IndexOf("_exploration.Reset(", StringComparison.Ordinal);
        int all = pass.IndexOf("RevealAll()", StringComparison.Ordinal);
        int reveal = pass.IndexOf("_exploration.Reveal(focus", StringComparison.Ordinal);
        Assert.True(reset < all && all < reveal);
    }

    [Fact]
    public void The_Enabled_Toggle_Is_Read_Every_Frame_And_Exploration_Keeps_Learning_While_Off()
    {
        string source = ReadWarMistSource();
        string pass = Method(source, "private void DrawWarMistPass(");

        // Read live from the settings object, never cached in a scene field.
        Assert.DoesNotMatch(@"(?m)^\s*(private|internal|public)[^;(]*\bbool\b[^;(]*WarMist[A-Za-z]*Enabled", source);
        Assert.Single(Regex.Matches(pass, @"_settings\.WarMistEnabled"));
        int toggle = pass.IndexOf("_settings.WarMistEnabled", StringComparison.Ordinal);
        int reveal = pass.IndexOf("_exploration.Reveal(focus", StringComparison.Ordinal);
        int draw = pass.IndexOf("_warMistRenderer.Draw(", StringComparison.Ordinal);
        Assert.True(reveal < toggle && toggle < draw);
        Assert.Matches(@"if \(!_settings\.WarMistEnabled\)\s*\{\s*return;", pass);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.25f)]
    [InlineData(1f)]
    public void The_Wall_Is_Opaque_At_And_Past_Every_Edge_And_Corner(float noise)
    {
        const float w = 56f;
        const float d = 40f;
        (float X, float Z)[] points =
        [
            (0f, 20f), (-3f, 20f), (-50f, 20f),
            (w, 20f), (w + 3f, 20f), (w + 50f, 20f),
            (28f, 0f), (28f, -3f), (28f, -50f),
            (28f, d), (28f, d + 3f), (28f, d + 50f),
            (0f, 0f), (w, 0f), (0f, d), (w, d),
            (-5f, -5f), (w + 5f, d + 5f), (-5f, d + 5f), (w + 5f, -5f),
        ];

        foreach ((float x, float z) in points)
        {
            Assert.Equal(1f, BoundaryMistField.Opacity(BoundaryMistField.Inset(x, z, w, d), noise));
        }
    }

    [Fact]
    public void Just_Inside_The_Edge_On_Every_Side_The_Wall_Is_Still_Nearly_Solid()
    {
        const float w = 56f;
        const float d = 40f;
        foreach ((float x, float z) in new[] { (0.01f, 20f), (w - 0.01f, 20f), (28f, 0.01f), (28f, d - 0.01f) })
        {
            Assert.True(BoundaryMistField.Opacity(BoundaryMistField.Inset(x, z, w, d), 0.5f) > 0.9f);
        }
    }

    [Fact]
    public void War_Mist_Density_Is_Exactly_Zero_Everywhere_Inside_The_Clear_Radius_For_Any_Input()
    {
        Assert.Equal(3.5f, WarMistField.ClearRadius);
        for (float distance = 0f; distance <= WarMistField.ClearRadius; distance += 0.05f)
        {
            foreach (float explored in new[] { 0f, 0.5f, 1f, -1f, 2f })
            {
                foreach (float noise in new[] { 0f, 1f, 3f, WarMistField.NoiseModMax })
                {
                    Assert.Equal(0f, WarMistField.TargetDensity(distance, explored, noise));
                }
            }

            Assert.Equal(0f, WarMistField.SliceAlpha(WarMistField.TargetDensity(distance, 0f, 1f), 6));
        }

        Assert.Equal(0f, WarMistField.Veil(WarMistField.ClearRadius));
        Assert.True(WarMistField.TargetDensity(WarMistField.ClearRadius + 0.5f, 0f, 1f) > 0f);
    }

    private static string ReadWarMistSource()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "dream", "dream.csproj")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory.FullName, "dream", "Client", "Scenes", "PlayScene.WarMist.cs"));
    }

    private static string Method(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature);
        int depth = 0;
        for (int i = source.IndexOf('{', start); i < source.Length; i++)
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

        throw new InvalidOperationException(signature);
    }
}
