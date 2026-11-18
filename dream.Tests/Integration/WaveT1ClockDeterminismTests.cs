using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Utilities;
using static Dream.Tests.Integration.PersistentWorldSeedDeterminismIntegrationTests;

namespace Dream.Tests.Integration;

// Wave T1 tester: the world clock itself must not reach the lockstep checksums, the snapshot bytes or
// the tick's allocations; only the join grows. Since wave T2 the clock is a gameplay input (a second
// wolf at night, WaveT2NightSpawnTests), so clocks that differ only within one phase still agree,
// and a night crossing allocates only on its spawn and despawn ticks.
public sealed class WaveT1ClockDeterminismTests(ITestOutputHelper output)
{
    // Two production persistent servers on copies of one world (same stored seed), the second with a
    // very different persisted clock and a 1-minute day, run the wave 13 combat/build/fracture script
    // in lockstep past the periodic checkpoint. Every tick's SimulationStateChecksum and the final
    // WorldStateChecksum match; each database then holds its own clock.
    //
    // Wave T2 made the phase a gameplay input (Night adds a wolf), so both clocks are chosen to stay
    // in Day for the whole run, and the test asserts that they do: the times of day differ, the phase
    // does not. Day against Night diverges by design (WaveT2NightSpawnTests).
    [Fact]
    public void TwoPersistentServers_WithDifferentClocks_StayInLockstep()
    {
        const int ticks = 400;
        using TempDatabase left = new();
        using TempDatabase right = new();
        using (ServerPersistenceBundle bundle = left.OpenBundle())
        {
            Assert.NotNull(bundle.SimulationSeed);
        }

        DatabaseImageCopy.Seed(left.DatabasePath, right.DatabasePath);

        const long rightPersisted = 7 * 115_200L + 50_000L;
        using (ServerPersistenceBundle bundle = right.OpenBundle())
        {
            new SqlitePersistenceCheckpointWriter(bundle.Schema).Write(PersistenceCheckpoint.Empty with { WorldTimeTicks = rightPersisted });
        }

        SqlitePools.Release(left.DatabasePath, right.DatabasePath);
        long leftFinal;
        long rightFinal;
        ServerPersistenceBundle rightBundle = right.OpenBundle();
        Assert.Equal(rightPersisted, rightBundle.WorldTimeTicks);
        // The host constructor of PersistentScriptedServer cannot seed, and the left server (built from a
        // bundle) is on the baseline: this states it for the right one too (a new character is Strength 10
        // already, so it is a no-op now) so the two cannot diverge on damage instead of the clock.
        PersistentScriptedServer.SeedBaseline(rightBundle);
        DedicatedServerHost rightHost = ServerBootstrap.CreateAuthoritativeHost(rightBundle);
        // A different day length on top of the different world time (before the first tick).
        rightHost.Runtime!.ConfigureWorldClock(WorldClockSettings.FromDayLengthMinutes(1), rightPersisted);

        using (PersistentScriptedServer first = new(left.OpenBundle()))
        using (PersistentScriptedServer second = new(rightHost))
        {
            Assert.Equal(first.Seed, second.Seed);
            Assert.NotEqual(first.Runtime.WorldClock.TimeOfDay01, second.Runtime.WorldClock.TimeOfDay01);
            Assert.Equal(WorldDayPhase.Day, first.Runtime.WorldClock.Phase);
            Assert.Equal(WorldDayPhase.Day, second.Runtime.WorldClock.Phase);
            Assert.Equal(first.Hash(), second.Hash());
            int firstDivergence = -1;
            for (int tick = 1; tick <= ticks; tick++)
            {
                ScriptStep step = Script(tick, first);
                first.Advance(tick, step);
                second.Advance(tick, step);
                Assert.Equal(WorldDayPhase.Day, first.Runtime.WorldClock.Phase);
                Assert.Equal(WorldDayPhase.Day, second.Runtime.WorldClock.Phase);
                if (firstDivergence < 0 && first.Hash() != second.Hash())
                {
                    firstDivergence = tick;
                    output.WriteLine($"tick {tick}: {first.CurrentParts} vs {second.CurrentParts}");
                }
            }

            Assert.True(firstDivergence < 0, $"The servers diverged at tick {firstDivergence}.");
            Assert.Equal(WorldStateChecksum.Compute(first.Runtime.Map), WorldStateChecksum.Compute(second.Runtime.Map));
            Assert.Equal(first.Record.HitTicks, second.Record.HitTicks);
            Assert.True(first.Record.HitTicks.Count > 0, "the script never rolled a hit");
            leftFinal = first.Runtime.WorldClock.WorldTicks;
            rightFinal = second.Runtime.WorldClock.WorldTicks;
            Assert.Equal(rightPersisted - 0L, rightFinal - leftFinal);
        }

        SqlitePools.Release(left.DatabasePath, right.DatabasePath);
        using (ServerPersistenceBundle reopened = left.OpenBundle())
        {
            Assert.Equal(leftFinal, reopened.WorldTimeTicks);
        }

        using (ServerPersistenceBundle reopened = right.OpenBundle())
        {
            Assert.Equal(rightFinal, reopened.WorldTimeTicks);
        }
    }

    // Snapshots carry no clock: two runtimes that differ only in their clock (both in Day, so the
    // night wolf is not a difference) export byte-identical snapshot payloads every tick.
    [Fact]
    public void SnapshotBytes_DoNotDependOnTheClock()
    {
        using WorldSessionRuntime first = CreateRuntime();
        using WorldSessionRuntime second = CreateRuntime();
        second.ConfigureWorldClock(WorldClockSettings.FromDayLengthMinutes(1), worldTicks: 987_654);
        TransportJsonCodec codec = new();
        long totalBytes = 0;
        for (ulong tick = 1; tick <= 200; tick++)
        {
            Tick(first, tick);
            Tick(second, tick);
            Assert.Equal(WorldDayPhase.Day, first.WorldClock.Phase);
            Assert.Equal(WorldDayPhase.Day, second.WorldClock.Phase);
            byte[] left = Encode(codec, first.ExportServerSnapshotEnvelope(), tick);
            byte[] right = Encode(codec, second.ExportServerSnapshotEnvelope(), tick);
            Assert.Equal(left.Length, right.Length);
            Assert.True(left.AsSpan().SequenceEqual(right), $"snapshot bytes differ at tick {tick}");
            Assert.DoesNotContain("worldClock", System.Text.Encoding.UTF8.GetString(left), StringComparison.OrdinalIgnoreCase);
            totalBytes += left.Length;
        }

        Assert.NotEqual(first.WorldClock.TimeOfDay01, second.WorldClock.TimeOfDay01);
        output.WriteLine($"200 snapshots, {totalBytes} B each side");
    }

    // The join grows by exactly the anchor's JSON field and nothing else.
    [Fact]
    public void JoinAccepted_GrowsByExactlyTheAnchorField()
    {
        using RuntimeAuthoritativeSessionHost host = RuntimeAuthoritativeSessionHost.CreateDefault();
        host.Runtime.ConfigureWorldClock(WorldClockSettings.Default, worldTicks: 1_234_567);
        JoinAcceptedMessage join = host.JoinSession("session-a", "Player A");
        WorldClockAnchor anchor = Assert.NotNull(join.WorldClock);
        TransportJsonCodec codec = new();
        TransportEnvelope envelope = new(TransportEnvelope.CurrentProtocolVersion, join.MessageKind, 1);

        byte[] withClock = codec.Encode(envelope, join);
        byte[] withoutClock = codec.Encode(envelope, join with { WorldClock = null });
        string field = $",\"worldClock\":{{\"serverTick\":{anchor.ServerTick},\"worldTicks\":{anchor.WorldTicks},\"ticksPerDay\":{anchor.TicksPerDay},\"startOffsetTicks\":{anchor.StartOffsetTicks}}}";

        output.WriteLine($"join {withoutClock.Length} B -> {withClock.Length} B (+{withClock.Length - withoutClock.Length}); field {field}");
        Assert.Equal(field.Length, withClock.Length - withoutClock.Length);
        Assert.Contains(field, System.Text.Encoding.UTF8.GetString(withClock));
    }

    // The 0 B tick (MEM-11) still holds with the clock: over a whole 1-minute day of ticks (all four
    // phase bounds and midnight, each raising PhaseChanged to a subscriber), measured tick by tick,
    // every tick allocates nothing on the tick thread except the two the night wolf needs: its spawn
    // on the first Night tick and its despawn (the queued row delete) on the first Dawn tick.
    [Fact]
    public void AWholeDayOfTicks_AcrossEveryPhaseBound_AllocatesNothing()
    {
        Assert.False(NetworkDiagnosticsLog.IsEnabled, "needs DREAM_NET_DIAGNOSTICS unset");
        WorldClockSettings settings = WorldClockSettings.FromDayLengthMinutes(1);

        using WorldSessionRuntime runtime = new(WorldDefaults.StartupFloorSize, new Vector3(40f, 1.5f, 40f), new SceneEventHub());
        runtime.ConfigureWorldClock(settings, 0);
        foreach (string session in new[] { "session-a", "session-b", "session-c" })
        {
            runtime.AttachRemoteSession(new PlayerSessionId(session), session);
        }

        int phaseChanges = 0;
        runtime.WorldClock.PhaseChanged += (_, _) => phaseChanges++;
        ulong tick = 0;
        for (int i = 0; i < 240; i++)
        {
            Tick(runtime, ++tick);
        }

        runtime.ExportServerSnapshotEnvelope();
        int dayTicks = (int)settings.TicksPerDay;

        // Tiered compilation charges a few hundred bytes to the calling thread whenever a tier-up,
        // call-counting or OSR transition lands inside a measured tick (see AllocationAssert). Under
        // full-suite load the 100 ms tiering delay expires mid-measurement, so one pass over a day
        // can read non-zero on an arbitrary tick although the tick code allocates nothing. GC.GetAllocatedBytesForCurrentThread
        // counts only this thread; the noise is the runtime's own work on it. Therefore: one whole
        // day of warm-up (every phase bound, spawn and despawn included), then two measured days,
        // keeping the minimum per day-relative tick. Noise only ever adds bytes, and the code under
        // test allocates the same on every day, so a real allocation shows in both days.
        for (int i = 0; i < dayTicks; i++)
        {
            Tick(runtime, ++tick);
        }

        int changesBefore = phaseChanges;
        long dayBefore = runtime.WorldClock.DayNumber;
        const int measuredDays = 2;
        long[] tickBytes = new long[dayTicks];
        Array.Fill(tickBytes, long.MaxValue);
        WorldDayPhase[] tickPhases = new WorldDayPhase[dayTicks];
        int[] tickWolves = new int[dayTicks];
        for (int day = 0; day < measuredDays; day++)
        {
            for (int i = 0; i < dayTicks; i++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                Tick(runtime, ++tick);
                long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                tickBytes[i] = Math.Min(tickBytes[i], bytes);
                tickPhases[i] = runtime.WorldClock.Phase;
                tickWolves[i] = runtime.Enemies.ActiveActors.Count;
            }
        }

        int spawnTick = -1;
        int despawnTick = -1;
        for (int i = 1; i < dayTicks; i++)
        {
            if (tickPhases[i] == WorldDayPhase.Night && tickPhases[i - 1] == WorldDayPhase.Dusk)
            {
                spawnTick = i;
            }
            else if (tickPhases[i] == WorldDayPhase.Dawn && tickPhases[i - 1] == WorldDayPhase.Night)
            {
                despawnTick = i;
            }
        }

        int[] allocatingTicks = Enumerable.Range(0, dayTicks).Where(i => tickBytes[i] != 0).ToArray();
        output.WriteLine(
            $"{settings.TicksPerDay} ticks per day ({measuredDays} measured days, {phaseChanges - changesBefore} phase changes): {tickBytes.Sum()} B; " +
            $"allocating ticks {string.Join(", ", allocatingTicks.Select(i => $"{i} ({tickPhases[i]}, {tickBytes[i]} B)"))}");
        Assert.Equal(4 * measuredDays, phaseChanges - changesBefore);
        Assert.Equal(dayBefore + measuredDays, runtime.WorldClock.DayNumber);
        Assert.True(spawnTick > 0 && despawnTick > 0, "the day did not cross nightfall and dawn");
        Assert.Equal(2, tickWolves[spawnTick]);
        Assert.Equal(1, tickWolves[spawnTick - 1]);
        Assert.Equal(1, tickWolves[despawnTick]);
        Assert.Equal(2, tickWolves[despawnTick - 1]);
        // The despawn may allocate (its queued row delete) or not; no other tick may.
        Assert.Contains(spawnTick, allocatingTicks);
        Assert.All(allocatingTicks, i => Assert.True(i == spawnTick || i == despawnTick, $"tick {i} ({tickPhases[i]}) allocated {tickBytes[i]} B"));
    }

    private static byte[] Encode(TransportJsonCodec codec, ServerSnapshotEnvelope snapshot, ulong sequence)
    {
        ServerSnapshotMessage message = new(snapshot);
        return codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, sequence), message);
    }

    private static WorldSessionRuntime CreateRuntime()
    {
        WorldSessionRuntime runtime = new(WorldDefaults.StartupFloorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        runtime.AttachRemoteSession(new PlayerSessionId("session-a"), "session-a");
        runtime.AttachRemoteSession(new PlayerSessionId("session-b"), "session-b");
        return runtime;
    }

    private static void Tick(WorldSessionRuntime runtime, ulong tick)
    {
        runtime.AdvanceAuthoritativeTick(
            tick,
            SimulationClock.FixedDeltaSeconds,
            default(PlayerCommandFrame),
            PlayerSimulationTuning.AuthoritativeDefault);
    }
}
