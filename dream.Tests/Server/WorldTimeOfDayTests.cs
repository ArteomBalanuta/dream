using Dream.Server;
using Dream.Simulation.World;
using Xunit;

namespace Dream.Tests.Server;

// DREAM_TIME_OF_DAY: the pure parser and the forward-only tick arithmetic.
public sealed class WorldTimeOfDayTests
{
    [Theory]
    [InlineData("22:30", 22.5)]
    [InlineData("6:00", 6d)]
    [InlineData("06:05", 6d + 5d / 60d)]
    [InlineData("0:00", 0d)]
    [InlineData("23:59", 23d + 59d / 60d)]
    [InlineData("13.5", 13.5)]
    [InlineData("0", 0d)]
    [InlineData("7", 7d)]
    [InlineData("23.99", 23.99)]
    [InlineData("  Noon ", 12d)]
    [InlineData("DAWN", 6d)]
    [InlineData("Morning", 8d)]
    [InlineData("dusk", 18d)]
    [InlineData("evening", 18d)]
    [InlineData("MidNight", 0d)]
    [InlineData("night", 22d)]
    public void TryParse_AcceptsFormatsAndNames(string text, double expected)
    {
        Assert.True(WorldTimeOfDay.TryParse(text, out double hours));
        Assert.Equal(expected, hours, precision: 9);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("24:00")]
    [InlineData("12:60")]
    [InlineData("12:5x")]
    [InlineData("12:")]
    [InlineData(":30")]
    [InlineData("1:2:3")]
    [InlineData("-1")]
    [InlineData("24")]
    [InlineData("25.5")]
    [InlineData("NaN")]
    [InlineData("1e1")]
    [InlineData("12,5")]
    [InlineData("lunch")]
    public void TryParse_RejectsInvalid(string? text)
    {
        Assert.False(WorldTimeOfDay.TryParse(text, out _));
    }

    [Theory]
    [InlineData("dawn", WorldDayPhase.Dawn)]
    [InlineData("morning", WorldDayPhase.Day)]
    [InlineData("noon", WorldDayPhase.Day)]
    [InlineData("dusk", WorldDayPhase.Dusk)]
    [InlineData("evening", WorldDayPhase.Dusk)]
    [InlineData("midnight", WorldDayPhase.Night)]
    [InlineData("night", WorldDayPhase.Night)]
    public void NamedTimes_LandInTheirDayPhase(string name, WorldDayPhase phase)
    {
        Assert.True(WorldTimeOfDay.TryParse(name, out double hours));
        Assert.Equal(phase, WorldDayPhases.At(hours / 24d));
    }

    [Theory]
    [InlineData(22.5, "22:30")]
    [InlineData(6d, "06:00")]
    [InlineData(13.5, "13:30")]
    [InlineData(23.999, "00:00")]
    public void Format_RoundsToTheMinute(double hours, string expected)
    {
        Assert.Equal(expected, WorldTimeOfDay.Format(hours));
    }

    [Fact]
    public void ForwardTicks_MovesForwardToTheTarget_ThisDay()
    {
        WorldClockSettings settings = WorldClockSettings.Default; // new world reads 08:00
        long forward = WorldTimeOfDay.ForwardTicks(settings, 0, 22.5);

        Assert.Equal(14.5 / 24d * settings.TicksPerDay, forward, precision: 6);
        Assert.Equal(22.5 / 24d, new WorldClock(settings, forward).TimeOfDay01, precision: 9);
    }

    [Fact]
    public void ForwardTicks_WrapsToTheNextDay_WhenTheTargetIsEarlier()
    {
        WorldClockSettings settings = WorldClockSettings.Default;
        long forward = WorldTimeOfDay.ForwardTicks(settings, 0, 6d); // 08:00 -> next 06:00

        Assert.Equal(22d / 24d * settings.TicksPerDay, forward, precision: 6);
        WorldClock clock = new(settings, forward);
        Assert.Equal(6d / 24d, clock.TimeOfDay01, precision: 9);
        Assert.Equal(1L, clock.DayNumber);
    }

    [Fact]
    public void ForwardTicks_IsZeroForAnExactMatch()
    {
        WorldClockSettings settings = WorldClockSettings.Default;

        Assert.Equal(0L, WorldTimeOfDay.ForwardTicks(settings, 0, 8d));
        long stored = 5 * settings.TicksPerDay + 1234;
        long target = (stored + settings.StartOffsetTicks) % settings.TicksPerDay;
        Assert.Equal(0L, WorldTimeOfDay.ForwardTicks(settings, stored, target / (double)settings.TicksPerDay * 24d));
    }

    [Fact]
    public void ForwardTicks_OneTickPastTheTargetWaitsAlmostAFullDay()
    {
        WorldClockSettings settings = WorldClockSettings.Default;

        Assert.Equal(settings.TicksPerDay - 1, WorldTimeOfDay.ForwardTicks(settings, 1, 8d));
    }

    [Theory]
    [InlineData(1d, 0L, 22.5)]
    [InlineData(1d, 123L, 6d)]
    [InlineData(7.5, 1_000_000L, 13.5)]
    [InlineData(1440d, 99L, 0d)]
    public void ForwardTicks_WorksForOtherDayLengths(double minutes, long worldTicks, double hours)
    {
        WorldClockSettings settings = WorldClockSettings.FromDayLengthMinutes(minutes);
        long forward = WorldTimeOfDay.ForwardTicks(settings, worldTicks, hours);

        Assert.InRange(forward, 0L, settings.TicksPerDay - 1);
        double expected = Math.Round(hours / 24d * settings.TicksPerDay) % settings.TicksPerDay / settings.TicksPerDay;
        Assert.Equal(expected, new WorldClock(settings, worldTicks + forward).TimeOfDay01, precision: 12);
    }

    [Fact]
    public void ResolveStartWorldTicks_UnsetOrBlankChangesNothing_InvalidLogsOneLine()
    {
        WorldClockSettings settings = WorldClockSettings.Default;
        List<string> lines = [];

        Assert.Equal(77L, ServerBootstrap.ResolveStartWorldTicks(settings, 77, null, lines.Add));
        Assert.Equal(77L, ServerBootstrap.ResolveStartWorldTicks(settings, 77, "  ", lines.Add));
        Assert.Empty(lines);

        Assert.Equal(77L, ServerBootstrap.ResolveStartWorldTicks(settings, 77, "lunch", lines.Add));
        string line = Assert.Single(lines);
        Assert.Contains(ServerBootstrap.TimeOfDayEnvironmentVariable, line);
    }

    [Fact]
    public void ResolveStartWorldTicks_MovesForwardAndLogsTheTime()
    {
        WorldClockSettings settings = WorldClockSettings.Default;
        List<string> lines = [];

        long ticks = ServerBootstrap.ResolveStartWorldTicks(settings, 0, " 22:30 ", lines.Add);

        Assert.Equal(WorldTimeOfDay.ForwardTicks(settings, 0, 22.5), ticks);
        Assert.Equal("World time set to 22:30 by DREAM_TIME_OF_DAY", Assert.Single(lines));
    }
}
