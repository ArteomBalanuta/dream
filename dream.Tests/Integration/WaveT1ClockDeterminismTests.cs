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

// Wave T1 tester: the world clock is presentation state. After the merge it must not reach the
// lockstep checksums, the snapshot bytes or the tick's allocations; only the join grows.
public sealed class WaveT1ClockDeterminismTests(ITestOutputHelper output)
{
    // Two production persistent servers on copies of one world (same stored seed), the second with a
    // very different persisted clock and a 1-minute day, run the wave 13 combat/build/fracture script
    // in lockstep past the periodic checkpoint. Every tick's SimulationStateChecksum and the final
    // WorldStateChecksum match; each database then holds its own clock.
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

        SqliteConnection.ClearAllPools();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            if (File.Exists(left.DatabasePath + suffix))
            {
                File.Copy(left.DatabasePath + suffix, right.DatabasePath + suffix);
            }
        }

        const long rightPersisted = 7 * 115_200L + 50_000L;
        using (ServerPersistenceBundle bundle = right.OpenBundle())
        {
            new SqlitePersistenceCheckpointWriter(bundle.Schema).Write(PersistenceCheckpoint.Empty with { WorldTimeTicks = rightPersisted });
        }

        SqliteConnection.ClearAllPools();
        long leftFinal;
        long rightFinal;
        ServerPersistenceBundle rightBundle = right.OpenBundle();
        Assert.Equal(rightPersisted, rightBundle.WorldTimeTicks);
        DedicatedServerHost rightHost = ServerBootstrap.CreateAuthoritativeHost(rightBundle);
        // A different day length on top of the different world time (before the first tick).
        rightHost.Runtime!.ConfigureWorldClock(WorldClockSettings.FromDayLengthMinutes(1), rightPersisted);

        using (PersistentScriptedServer first = new(left.OpenBundle()))
        using (PersistentScriptedServer second = new(rightHost))
        {
            Assert.Equal(first.Seed, second.Seed);
            Assert.NotEqual(first.Runtime.WorldClock.TimeOfDay01, second.Runtime.WorldClock.TimeOfDay01);
            Assert.Equal(first.Hash(), second.Hash());
            int firstDivergence = -1;
            for (int tick = 1; tick <= ticks; tick++)
            {
                ScriptStep step = Script(tick, first);
                first.Advance(tick, step);
                second.Advance(tick, step);
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

        SqliteConnection.ClearAllPools();
        using (ServerPersistenceBundle reopened = left.OpenBundle())
        {
            Assert.Equal(leftFinal, reopened.WorldTimeTicks);
        }

        using (ServerPersistenceBundle reopened = right.OpenBundle())
        {
            Assert.Equal(rightFinal, reopened.WorldTimeTicks);
        }
    }

    // Snapshots carry no clock: two runtimes that differ only in their clock export byte-identical
    // snapshot payloads every tick.
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

    // The 0 B tick (MEM-11) still holds with the clock: a whole 1-minute day of ticks (all four
    // phase bounds and midnight, each raising PhaseChanged to a subscriber) measured in one window
    // allocates nothing on the tick thread.
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
        int changesBefore = phaseChanges;
        long dayBefore = runtime.WorldClock.DayNumber;

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (long i = 0; i < settings.TicksPerDay; i++)
        {
            Tick(runtime, ++tick);
        }

        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;

        output.WriteLine($"{settings.TicksPerDay} ticks (one day, {phaseChanges - changesBefore} phase changes): {bytes} B");
        Assert.Equal(4, phaseChanges - changesBefore);
        Assert.Equal(dayBefore + 1, runtime.WorldClock.DayNumber);
        Assert.Equal(0, bytes);
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
