using Dream.Rendering.Mist;
using Xunit;

namespace Dream.Tests.Rendering.Mist;

public class WarMistFieldTests
{
    [Theory]
    [InlineData(0f)]
    [InlineData(3f)]
    [InlineData(3.5f)]
    public void Nothing_Inside_The_Clear_Radius(float distance)
    {
        Assert.Equal(0f, WarMistField.TargetDensity(distance, 0f, WarMistField.NoiseModMax));
        Assert.Equal(0f, WarMistField.TargetDensity(distance, 1f, WarMistField.NoiseModMax));
    }

    [Fact]
    public void Unexplored_Is_Denser_Than_Explored_Beyond_The_Full_Radius()
    {
        foreach (float distance in new[] { 7.5f, 15f, 30f })
        {
            float unexplored = WarMistField.TargetDensity(distance, 0f, 1f);
            float explored = WarMistField.TargetDensity(distance, 1f, 1f);
            Assert.True(unexplored > explored, $"distance {distance}");
            Assert.Equal(WarMistField.UnexploredDensity, unexplored, 1e-5f);
            Assert.Equal(WarMistField.ExploredDensity, explored, 1e-5f);
        }
    }

    [Fact]
    public void The_Veil_Grows_Monotonically_With_Distance()
    {
        float previous = -1f;
        for (float distance = 0f; distance <= 20f; distance += 0.25f)
        {
            float density = WarMistField.TargetDensity(distance, 0f, 1f);
            Assert.True(density >= previous);
            previous = density;
        }
    }

    [Fact]
    public void Density_Never_Reaches_Full_Opacity()
    {
        Assert.True(WarMistField.TargetDensity(40f, 0f, 100f) <= WarMistField.MaxTargetDensity);
        Assert.True(WarMistField.TargetDensity(40f, 0f, 1f) < 1f);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    public void Slices_Compose_To_The_Target_Within_A_Thousandth(int sliceCount)
    {
        for (float target = 0f; target <= WarMistField.MaxTargetDensity; target += 0.05f)
        {
            float alpha = WarMistField.SliceAlpha(target, sliceCount);
            Assert.InRange(WarMistField.Compose(alpha, sliceCount), target - 1e-3f, target + 1e-3f);
        }
    }

    [Fact]
    public void Six_Slices_Match_The_Reference_Stack_Above_The_Floor()
    {
        float[] heights = WarMistField.CreateSliceHeights(6);

        Assert.Equal(WarMistSettings.MaxSlices, heights.Length);
        float[] expected = [1.10f, 1.60f, 2.20f, 2.90f, 3.70f, 4.60f];
        for (int i = 0; i < 6; i++)
        {
            Assert.Equal(expected[i], heights[i], 1e-5f);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    public void Other_Stacks_Run_Bottom_First_From_The_Same_Span(int sliceCount)
    {
        float[] heights = WarMistField.CreateSliceHeights(sliceCount);

        Assert.Equal(1.10f, heights[0], 1e-5f);
        Assert.Equal(4.60f, heights[sliceCount - 1], 1e-5f);
        for (int i = 1; i < sliceCount; i++)
        {
            Assert.True(heights[i] > heights[i - 1]);
        }
    }
}

public class WarMistSettingsTests
{
    [Fact]
    public void Unset_Gives_The_Defaults_And_No_Warning()
    {
        List<string> warnings = [];

        WarMistSettings settings = WarMistSettings.Parse(null, "", "  ", warnings.Add);

        Assert.Equal(new WarMistSettings(true, false, 6), settings);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Valid_Values_Are_Read()
    {
        WarMistSettings settings = WarMistSettings.Parse("0", "all", "3", null);

        Assert.Equal(new WarMistSettings(false, true, 3), settings);
    }

    [Theory]
    [InlineData("2", 2)]
    [InlineData("8", 8)]
    public void Slice_Count_Bounds_Are_Inclusive(string value, int expected)
    {
        Assert.Equal(expected, WarMistSettings.Parse(null, null, value, null).SliceCount);
    }

    [Theory]
    [InlineData("yes", "maybe", "9", 3)]
    [InlineData("2", "ALL?", "1", 3)]
    [InlineData("on", "none", "six", 2)]
    public void Invalid_Values_Warn_Once_Each_And_Fall_Back(string enabled, string explored, string slices, int expectedWarnings)
    {
        List<string> warnings = [];

        WarMistSettings settings = WarMistSettings.Parse(enabled, explored, slices, warnings.Add);

        Assert.Equal(WarMistSettings.Default, settings);
        Assert.Equal(expectedWarnings, warnings.Count);
        Assert.All(warnings, line => Assert.Contains("DREAM_WAR_MIST", line));
    }
}
