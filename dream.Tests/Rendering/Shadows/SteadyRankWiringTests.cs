using System.Text.RegularExpressions;
using Xunit;

namespace Dream.Tests.Rendering.Shadows;

// Render hands the producers' side arrays (flicker, wander) to the selector and the slot filler. If either call drops an
// array the ranking silently falls back to the unsteady score, and no behavioural test of the pure functions notices.
public class SteadyRankWiringTests
{
    private const string SelectPattern = @"SelectNearestFaded\([^;]*_selectedLightFlicker[^;]*_selectedLightWander[^;]*\)\s*;";
    private const string FillPattern = @"FireShadowAtlas\.FillSlots\([^;]*_selectedLightFlicker[^;]*_selectedLightWander[^;]*\)\s*;";

    [Fact]
    public void Render_Passes_Both_Side_Arrays_To_SelectNearestFaded()
    {
        Assert.True(Passes(Strip(Read("dream", "Rendering", "Core", "Render.PointLights.cs")), SelectPattern));
    }

    [Fact]
    public void Render_Passes_Both_Side_Arrays_To_FillSlots()
    {
        Assert.True(Passes(Strip(Read("dream", "Rendering", "Core", "Render.Shadows.cs")), FillPattern));
    }

    [Fact]
    public void Synthetic_Negative_Mutated_Source_Fails_The_Check()
    {
        string select = Strip(Read("dream", "Rendering", "Core", "Render.PointLights.cs"));
        string fill = Strip(Read("dream", "Rendering", "Core", "Render.Shadows.cs"));
        Assert.False(Passes(select.Replace(", _selectedLightWander", string.Empty, StringComparison.Ordinal), SelectPattern));
        Assert.False(Passes(select.Replace("_selectedLightFlicker", "null", StringComparison.Ordinal), SelectPattern));
        Assert.False(Passes(fill.Replace(", _selectedLightWander", string.Empty, StringComparison.Ordinal), FillPattern));
        Assert.False(Passes(fill.Replace("_selectedLightFlicker, ", string.Empty, StringComparison.Ordinal), FillPattern));
    }

    private static bool Passes(string source, string pattern) => Regex.IsMatch(source, pattern);

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

    private static string Strip(string source) => Regex.Replace(source, @"//[^\r\n]*", string.Empty);

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
