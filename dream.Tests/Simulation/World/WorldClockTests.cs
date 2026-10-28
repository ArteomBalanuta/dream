using Dream.Multiplayer.Protocol;
using Dream.Simulation.Determinism;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Simulation.World;

// Wave T1a: the authoritative world clock. It counts ticks only, advances once per authoritative
// tick and stays out of both world checksums.
public sealed class WorldClockTests
{
    private const int FloorSize = 16;

    [Fact]
    public void DefaultSettings_AreThirtyMinuteDays_StartingAtEight()
    {
        Assert.Equal(115_200L, WorldClockSettings.Default.TicksPerDay);
        Assert.Equal(38_400L, WorldClockSettings.Default.StartOffsetTicks);
    }

    [Fact]
    public void NewWorld_ReadsEightOClockOnDayZero_InTheDayPhase()
    {
        WorldClock clock = new(WorldClockSettings.Default);

        Assert.Equal(0L, clock.WorldTicks);
        Assert.Equal(8d / 24d, clock.TimeOfDay01, precision: 12);
        Assert.Equal(0L, clock.DayNumber);
        Assert.Equal(WorldDayPhase.Day, clock.Phase);
    }

    [Fact]
    public void Advance_MovesOneTick_AndRollsOverAtMidnight()
    {
        WorldClockSettings settings = WorldClockSettings.Default;
        long ticksToMidnight = settings.TicksPerDay - settings.StartOffsetTicks;
        WorldClock clock = new(settings, ticksToMidnight - 1);
        Assert.Equal(0L, clock.DayNumber);
        Assert.Equal((settings.TicksPerDay - 1) / (double)settings.TicksPerDay, clock.TimeOfDay01, precision: 12);

        clock.Advance();

        Assert.Equal(ticksToMidnight, clock.WorldTicks);
        Assert.Equal(1L, clock.DayNumber);
        Assert.Equal(0d, clock.TimeOfDay01);

        clock.Advance();
        Assert.Equal(1d / settings.TicksPerDay, clock.TimeOfDay01, precision: 15);
    }

    [Fact]
    public void Constructor_ResumesAtTheGivenTicks_AndRejectsBadInput()
    {
        WorldClock resumed = new(WorldClockSettings.Default, worldTicks: 3 * 115_200L + 57_600L);
        Assert.Equal(3L, resumed.DayNumber);
        // 57,600 + 38,400 = 96,000 of 115,200: 20:00, the first tick of night.
        Assert.Equal(20d / 24d, resumed.TimeOfDay01, precision: 12);
        Assert.Equal(WorldDayPhase.Night, resumed.Phase);

        Assert.Throws<ArgumentOutOfRangeException>(() => new WorldClock(WorldClockSettings.Default, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorldClock(new WorldClockSettings(0, 0)));
    }

    [Theory]
    [InlineData(0.0, WorldDayPhase.Night)]
    [InlineData(3.999, WorldDayPhase.Night)]
    [InlineData(4.0, WorldDayPhase.Dawn)]
    [InlineData(6.999, WorldDayPhase.Dawn)]
    [InlineData(7.0, WorldDayPhase.Day)]
    [InlineData(12.0, WorldDayPhase.Day)]
    [InlineData(16.999, WorldDayPhase.Day)]
    [InlineData(17.0, WorldDayPhase.Dusk)]
    [InlineData(19.999, WorldDayPhase.Dusk)]
    [InlineData(20.0, WorldDayPhase.Night)]
    [InlineData(23.999, WorldDayPhase.Night)]
    public void PhaseBounds_AreInclusiveAtTheirStart(double hour, WorldDayPhase expected)
    {
        Assert.Equal(expected, WorldDayPhases.At(hour / 24d));
    }

    [Fact]
    public void AFullDay_RaisesOnePhaseEventPerBoundary_OnTheExactBoundaryTick()
    {
        WorldClockSettings settings = WorldClockSettings.Default;
        WorldClock clock = new(settings);
        List<(long Tick, WorldDayPhase Previous, WorldDayPhase Current)> events = [];
        clock.PhaseChanged += (previous, current) => events.Add((clock.WorldTicks, previous, current));

        for (long i = 0; i < settings.TicksPerDay; i++)
        {
            clock.Advance();
        }

        long TickAt(int hour) =>
            ((hour * settings.TicksPerDay / 24) - settings.StartOffsetTicks + settings.TicksPerDay) % settings.TicksPerDay;

        Assert.Equal(
            [
                (TickAt(17), WorldDayPhase.Day, WorldDayPhase.Dusk),
                (TickAt(20), WorldDayPhase.Dusk, WorldDayPhase.Night),
                (TickAt(4), WorldDayPhase.Night, WorldDayPhase.Dawn),
                (TickAt(7), WorldDayPhase.Dawn, WorldDayPhase.Day)
            ],
            events);
        // Night is 8 of 24 hours: 10 minutes of a 30-minute day.
        Assert.Equal(10 * 60 * SimulationClock.AuthoritativeTicksPerSecond, TickAt(4) - TickAt(20));
        Assert.Equal(WorldDayPhase.Day, clock.Phase);
    }

    [Fact]
    public void Advance_AllocatesNothing()
    {
        WorldClock clock = new(WorldClockSettings.FromDayLengthMinutes(1));
        int changes = 0;
        clock.PhaseChanged += (_, _) => changes++;

        AllocationAssert.AtMost(0, clock.Advance, iterations: 1000);
        Assert.True(changes > 0);
    }

    [Fact]
    public void FromDayLengthMinutes_ScalesTicksPerDay_AndKeepsTheEightOClockStart()
    {
        WorldClockSettings oneMinute = WorldClockSettings.FromDayLengthMinutes(1);
        Assert.Equal(3_840L, oneMinute.TicksPerDay);
        Assert.Equal(1_280L, oneMinute.StartOffsetTicks);
        Assert.Equal(8d / 24d, new WorldClock(oneMinute).TimeOfDay01, precision: 12);
    }

    [Fact]
    public void Runtime_AdvancesTheClockOncePerAuthoritativeTick_AndNotForQueuedActions()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        runtime.ConfigureWorldClock(WorldClockSettings.Default, worldTicks: 5_000);
        long offset = runtime.WorldClock.WorldTicks - (long)runtime.CurrentServerTick;

        for (ulong tick = 1; tick <= 200; tick++)
        {
            Tick(runtime, tick);
            runtime.ProcessQueuedAuthoritativeActions(tick, []);
            Assert.Equal(offset, runtime.WorldClock.WorldTicks - (long)runtime.CurrentServerTick);
        }

        Assert.Equal(5_200L, runtime.WorldClock.WorldTicks);
    }

    [Fact]
    public void ConfigureWorldClock_ReplacesSettingsAndTicks()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        Assert.Equal(WorldClockSettings.Default, runtime.WorldClock.Settings);
        Assert.Equal(0L, runtime.WorldClock.WorldTicks);

        WorldClockSettings settings = WorldClockSettings.FromDayLengthMinutes(5);
        runtime.ConfigureWorldClock(settings, worldTicks: 123);

        Assert.Equal(settings, runtime.WorldClock.Settings);
        Assert.Equal(123L, runtime.WorldClock.WorldTicks);
    }

    // The clock is presentation state: two runtimes that differ only in their clock give the same
    // simulation and world checksums after the same inputs.
    [Fact]
    public void DifferentClockStates_GiveIdenticalChecksums()
    {
        using WorldSessionRuntime first = CreateRuntime();
        using WorldSessionRuntime second = CreateRuntime();
        second.ConfigureWorldClock(WorldClockSettings.FromDayLengthMinutes(1), worldTicks: 987_654);

        for (ulong tick = 1; tick <= 256; tick++)
        {
            Tick(first, tick);
            Tick(second, tick);
            if (tick % 64 == 0)
            {
                Assert.Equal(SimulationStateChecksum.Compute(first), SimulationStateChecksum.Compute(second));
                Assert.Equal(WorldStateChecksum.Compute(first.Map), WorldStateChecksum.Compute(second.Map));
            }
        }

        Assert.NotEqual(first.WorldClock.WorldTicks, second.WorldClock.WorldTicks);
    }

    private static WorldSessionRuntime CreateRuntime()
    {
        return new WorldSessionRuntime(FloorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
    }

    private static void Tick(WorldSessionRuntime runtime, ulong tick)
    {
        runtime.AdvanceAuthoritativeTick(
            tick,
            SimulationClock.FixedDeltaSeconds,
            (PlayerCommandFrame?)null,
            PlayerSimulationTuning.AuthoritativeDefault);
    }
}
