using System.IO;
using Dream.Rendering.Mist;
using Dream.Simulation.Players;
using Xunit;

namespace Dream.Tests.Rendering.Mist;

public class BoundaryMistFieldTests
{
    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void The_Edge_And_Beyond_Are_Fully_Opaque_Whatever_The_Noise(float noise)
    {
        Assert.Equal(1f, BoundaryMistField.Opacity(0f, noise));
        Assert.Equal(1f, BoundaryMistField.Opacity(-0.001f, noise));
        Assert.Equal(1f, BoundaryMistField.Opacity(-40f, noise));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void Past_The_Ramp_Plus_Noise_The_Mist_Is_Gone(float noise)
    {
        float end = BoundaryMistField.RampStart + BoundaryMistField.NoiseAmp;
        Assert.Equal(0f, BoundaryMistField.Opacity(end, noise));
        Assert.Equal(0f, BoundaryMistField.Opacity(end + 10f, noise));
    }

    [Fact]
    public void Where_A_Player_Stops_The_Mist_Is_Thick_But_Not_Solid()
    {
        Assert.Equal(Player.EdgeMargin, BoundaryMistField.StopInset);

        // Median noise reads as the plan's ~0.63; the noise extremes stay inside a readable band.
        float mid = BoundaryMistField.Opacity(BoundaryMistField.StopInset, 0.5f);
        Assert.InRange(mid, 0.5f, 0.75f);
        foreach (float noise in new[] { 0f, 0.5f, 1f })
        {
            float opacity = BoundaryMistField.Opacity(BoundaryMistField.StopInset, noise);
            Assert.InRange(opacity, 0.4f, 0.9f);
        }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void Opacity_Never_Rises_With_Inset(float noise)
    {
        float previous = 1f;
        for (float d = -1f; d <= 5f; d += 0.01f)
        {
            float opacity = BoundaryMistField.Opacity(d, noise);
            Assert.InRange(opacity, 0f, 1f);
            Assert.True(opacity <= previous + 1e-5f, $"d={d}: {opacity} > {previous}");
            previous = opacity;
        }
    }

    [Fact]
    public void Inset_Is_Measured_To_The_Nearest_Of_The_Four_Sides()
    {
        const float w = 56f;
        const float d = 40f;
        Assert.Equal(2f, BoundaryMistField.Inset(2f, 20f, w, d));
        Assert.Equal(3f, BoundaryMistField.Inset(w - 3f, 20f, w, d));
        Assert.Equal(4f, BoundaryMistField.Inset(30f, 4f, w, d));
        Assert.Equal(1f, BoundaryMistField.Inset(30f, d - 1f, w, d));
        Assert.Equal(0f, BoundaryMistField.Inset(0f, 0f, w, d));
        Assert.Equal(0f, BoundaryMistField.Inset(w, d, w, d));
        Assert.Equal(-5f, BoundaryMistField.Inset(-5f, 10f, w, d));
        Assert.Equal(-7f, BoundaryMistField.Inset(w + 7f, d + 1f, w, d));
        Assert.Equal(20f, BoundaryMistField.Inset(28f, 20f, w, d));
    }

    [Fact]
    public void The_Wrapped_Time_Stays_In_Range()
    {
        Assert.Equal(0f, BoundaryMistField.WrapTime(0d));
        Assert.Equal(5f, BoundaryMistField.WrapTime(6405d));
        Assert.InRange((double)BoundaryMistField.WrapTime(1e7), 0d, BoundaryMistField.TimeWrapSeconds);
        Assert.InRange((double)BoundaryMistField.WrapTime(-3d), 0d, BoundaryMistField.TimeWrapSeconds);
    }

    [Fact]
    public void Every_Lattice_Rate_Times_The_Wrap_Is_A_Whole_Multiple_Of_The_Hash_Period()
    {
        double[] rates =
        [
            BoundaryMistField.EdgeRate.X, BoundaryMistField.EdgeRate.Z,
            BoundaryMistField.Octave1Rate.X, BoundaryMistField.Octave1Rate.Z,
            BoundaryMistField.Octave2Rate.X, BoundaryMistField.Octave2Rate.Z,
            BoundaryMistField.Octave3Rate,
        ];

        foreach (double rate in rates)
        {
            double periods = rate * BoundaryMistField.TimeWrapSeconds / BoundaryMistField.HashPeriod;
            Assert.True(Math.Abs(periods - Math.Round(periods)) < 1e-9, $"rate {rate} shifts {periods} periods per wrap");
        }
    }

    [Fact]
    public void The_Shader_Drifts_At_The_Rates_The_Field_Declares_And_Hashes_Periodically()
    {
        string shader = System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(Path.Combine(RepoRoot(), "dream", "shaders", "boundaryMist.glsl")), @"//[^\n]*", string.Empty);
        System.Globalization.CultureInfo c = System.Globalization.CultureInfo.InvariantCulture;

        Assert.Contains($"vec2({BoundaryMistField.EdgeRate.X.ToString("0.00", c)}, {BoundaryMistField.EdgeRate.Z.ToString("0.00", c)}) * uMistParams.y", shader);
        Assert.Contains($"vec2({BoundaryMistField.Octave1Rate.X.ToString("0.00", c)}, {BoundaryMistField.Octave1Rate.Z.ToString("0.00", c)}) * t", shader);
        Assert.Contains($"vec2({BoundaryMistField.Octave2Rate.X.ToString("0.00", c)}, {BoundaryMistField.Octave2Rate.Z.ToString("0.00", c)}) * t", shader);
        Assert.Contains($"t * {BoundaryMistField.Octave3Rate.ToString("0.0", c)}", shader);
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(shader, $@"dreamMistHash\(mod\(cell[^;]*, {BoundaryMistField.HashPeriod}\.0\)\)").Count);
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

public class BoundaryMistSettingsTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData(" 0 ", false)]
    public void Parses_Zero_And_One(string? value, bool expected)
    {
        List<string> warnings = [];
        Assert.Equal(expected, BoundaryMistSettings.Parse(value, warnings.Add).Enabled);
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("2")]
    public void An_Invalid_Value_Warns_Once_And_Keeps_The_Default(string value)
    {
        List<string> warnings = [];
        Assert.True(BoundaryMistSettings.Parse(value, warnings.Add).Enabled);
        Assert.Single(warnings);
    }
}
