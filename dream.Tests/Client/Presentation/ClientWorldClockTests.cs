using Xunit;
using Dream.Client.Presentation;
using Dream.Simulation.World;
using Dream.Tests.Utilities;

namespace Dream.Tests.Client.Presentation;

// T1b: the client derives the time of day from the join anchor and its own server tick estimate.
public class ClientWorldClockTests
{
    private static readonly WorldClockSettings Settings = WorldClockSettings.Default;

    [Fact]
    public void WithoutAnAnchor_ShowsTheStartTimeOfDay_OnDayZero()
    {
        ClientWorldClock clock = new();

        Assert.False(clock.HasAnchor);
        Assert.Equal((float)WorldClockSettings.StartTimeOfDay01, clock.TimeOfDay01(12345.5));
        Assert.Equal(0L, clock.DayNumber(12345.5));
    }

    // A new world (WorldTicks 0) at its anchor tick reads 08:00, like the server's clock.
    [Fact]
    public void NewWorld_AtTheAnchorTick_ReadsEightOClock()
    {
        ClientWorldClock clock = new();
        clock.Apply(new WorldClockAnchor(500, 0, Settings.TicksPerDay, Settings.StartOffsetTicks));

        Assert.True(clock.HasAnchor);
        Assert.Equal(8f / 24f, clock.TimeOfDay01(500), 6);
        Assert.Equal(0L, clock.DayNumber(500));
    }

    // The anchor math: (W + (t - T0) + S) mod D / D, at whole and fractional ticks, before and
    // after the anchor tick, with a non-zero persisted world time.
    [Theory]
    [InlineData(0L, 1000UL, 1000d)]
    [InlineData(0L, 1000UL, 1000.5d)]
    [InlineData(0L, 1000UL, 990.25d)]
    [InlineData(5_000_000L, 77UL, 12_345.75d)]
    [InlineData(115_199L, 0UL, 3d)]
    public void TimeOfDayAndDayNumber_FollowTheAnchorMath(long worldTicks, ulong anchorTick, double serverTick)
    {
        WorldClockAnchor anchor = new(anchorTick, worldTicks, Settings.TicksPerDay, Settings.StartOffsetTicks);
        ClientWorldClock clock = new();
        clock.Apply(anchor);

        double totalTicks = worldTicks + (serverTick - anchorTick) + Settings.StartOffsetTicks;
        double expectedDays = totalTicks / Settings.TicksPerDay;
        double expectedTime = expectedDays - Math.Floor(expectedDays);

        Assert.Equal(expectedTime, clock.TimeOfDay01(serverTick), 5);
        Assert.Equal((long)Math.Floor(expectedDays), clock.DayNumber(serverTick));
        Assert.Equal(worldTicks + (serverTick - anchorTick), anchor.WorldTicksAt(serverTick), 9);
    }

    // Across midnight the time wraps to 0 and the day number steps by one, tick by tick.
    [Fact]
    public void AcrossMidnight_TimeWrapsAndTheDayNumberStepsOnce()
    {
        // World ticks such that midnight of day 1 falls 10 ticks after the anchor tick.
        long midnightWorldTicks = Settings.TicksPerDay - Settings.StartOffsetTicks;
        ClientWorldClock clock = new();
        clock.Apply(new WorldClockAnchor(1000, midnightWorldTicks - 10, Settings.TicksPerDay, Settings.StartOffsetTicks));

        float previous = clock.TimeOfDay01(1000);
        int wraps = 0;
        for (int tick = 1001; tick <= 1020; tick++)
        {
            float time = clock.TimeOfDay01(tick);
            Assert.InRange(time, 0f, 1f);
            if (time < previous)
            {
                wraps++;
                Assert.Equal(1010, tick);
                Assert.Equal(0f, time);
            }

            previous = time;
        }

        Assert.Equal(1, wraps);
        Assert.Equal(0L, clock.DayNumber(1009.5));
        Assert.Equal(1L, clock.DayNumber(1010));
    }

    // The smallest day the server allows (24 ticks) wraps many times; every reading stays in [0, 1).
    [Fact]
    public void ShortDay_WrapsRepeatedly_AndStaysInRange()
    {
        WorldClockSettings shortDay = WorldClockSettings.FromDayLengthMinutes(0.001);
        Assert.Equal(24L, shortDay.TicksPerDay);
        ClientWorldClock clock = new();
        clock.Apply(new WorldClockAnchor(0, 0, shortDay.TicksPerDay, shortDay.StartOffsetTicks));

        for (double tick = 0; tick < 24 * 5; tick += 0.37)
        {
            float time = clock.TimeOfDay01(tick);
            Assert.True(time >= 0f && time <= 1f, $"time {time} at tick {tick}");
        }

        Assert.Equal(5L, clock.DayNumber(24 * 5 - shortDay.StartOffsetTicks));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-115_200L)]
    public void AnAnchorWithoutADayLength_IsIgnored_AndClearsTheClock(long ticksPerDay)
    {
        ClientWorldClock clock = new();
        clock.Apply(new WorldClockAnchor(10, 999, Settings.TicksPerDay, Settings.StartOffsetTicks));
        Assert.True(clock.HasAnchor);

        clock.Apply(new WorldClockAnchor(10, 999, ticksPerDay, Settings.StartOffsetTicks));

        Assert.False(clock.HasAnchor);
        Assert.Equal(default, clock.Anchor);
        Assert.Equal((float)WorldClockSettings.StartTimeOfDay01, clock.TimeOfDay01(500));
        Assert.Equal(0L, clock.DayNumber(500));
    }

    [Fact]
    public void Clear_ForgetsTheAnchor()
    {
        ClientWorldClock clock = new();
        clock.Apply(new WorldClockAnchor(10, 50_000, Settings.TicksPerDay, Settings.StartOffsetTicks));

        clock.Clear();

        Assert.False(clock.HasAnchor);
        Assert.Equal((float)WorldClockSettings.StartTimeOfDay01, clock.TimeOfDay01(10));
    }

    // Read once per frame by the renderer: no allocation.
    [Fact]
    public void Reading_AllocatesNothing()
    {
        ClientWorldClock clock = new();
        clock.Apply(new WorldClockAnchor(10, 50_000, Settings.TicksPerDay, Settings.StartOffsetTicks));
        double tick = 11d;
        float sink = 0f;

        AllocationAssert.AtMost(0, () =>
        {
            tick += 0.25d;
            sink += clock.TimeOfDay01(tick) + clock.DayNumber(tick);
        });

        Assert.True(float.IsFinite(sink));
    }
}
