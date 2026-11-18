using Dream.Rendering.Sky;
using Dream.Tests.Utilities;
using Xunit;

namespace Dream.Tests.Rendering.Sky;

public class TimeOfDayOverrideTests
{
    [Fact]
    public void Disabled_Override_Passes_The_Replicated_Time_Through()
    {
        TimeOfDayOverride timeOfDay = new() { TimeOfDay01 = 0.9f };

        Assert.False(timeOfDay.Enabled);
        Assert.Equal(0.25f, timeOfDay.Resolve(0.25f));
        Assert.Equal(0.75f, timeOfDay.Resolve(0.75f));
    }

    [Fact]
    public void Enabled_Override_Replaces_The_Replicated_Time()
    {
        TimeOfDayOverride timeOfDay = new() { Enabled = true, TimeOfDay01 = 0.9f };

        Assert.Equal(0.9f, timeOfDay.Resolve(0.25f));
        Assert.Equal(0.9f, timeOfDay.Resolve(0.75f));
    }

    [Fact]
    public void Defaults_To_Noon()
    {
        TimeOfDayOverride timeOfDay = new() { Enabled = true };

        Assert.Equal(0.5f, timeOfDay.Resolve(0f));
        Assert.Equal(12f, timeOfDay.Hours);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(6f, 0.25f)]
    [InlineData(18f, 0.75f)]
    [InlineData(24f, 0f)]
    [InlineData(30f, 0.25f)]
    [InlineData(-6f, 0.75f)]
    public void Hours_Map_Onto_A_Wrapped_Day(float hours, float expected01)
    {
        TimeOfDayOverride timeOfDay = new() { Hours = hours };

        Assert.Equal(expected01, timeOfDay.TimeOfDay01, 5);
        Assert.InRange(timeOfDay.TimeOfDay01, 0f, 0.99999994f);
    }

    [Fact]
    public void Tiny_Negative_Values_Wrap_Below_One()
    {
        TimeOfDayOverride timeOfDay = new() { TimeOfDay01 = -1e-9f };

        Assert.True(timeOfDay.TimeOfDay01 < 1f);
        Assert.True(timeOfDay.TimeOfDay01 >= 0f);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void Non_Finite_Values_Are_Ignored(float value)
    {
        TimeOfDayOverride timeOfDay = new() { TimeOfDay01 = 0.3f };

        timeOfDay.TimeOfDay01 = value;
        timeOfDay.Hours = value;

        Assert.Equal(0.3f, timeOfDay.TimeOfDay01);
    }

    [Fact]
    public void Resolve_Allocates_Nothing()
    {
        TimeOfDayOverride timeOfDay = new() { Enabled = true, TimeOfDay01 = 0.1f };
        float sink = 0f;

        long bytes = AllocationAssert.AtMost(0, () =>
        {
            timeOfDay.Enabled = !timeOfDay.Enabled;
            sink += timeOfDay.Resolve(0.4f);
        });

        Assert.Equal(0, bytes);
        GC.KeepAlive(sink);
    }
}
