using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Replication;
using Dream.Client.World;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Load;
using OpenTK.Mathematics;

namespace Dream.Tests.Integration;

// Persistence wave E, workstream D: a stored world survives long scripted play with fractures,
// placements in removed cells and restarts.
//
// Short (always on): a 30x8x30 stored world, 3 sessions of 3,000 ticks, a scripted fracture slot
// every 64 ticks (SoakServer: attack the nearest standing cube in reach, terrain raised and
// sculpture cubes and placed cubes alike; with none in reach, place a cube in a free cell beside the
// player, rubble cells first), restarts between sessions. After each restart: override rows equal
// the removed source cells, no orphan rubble, ids unique and below the counter, integrity_check ok,
// no invariant violation.
//
// Long (DREAM_LOAD_TEST=1): World10k stored, 115,200 ticks (30 simulated minutes), a slot every 128
// ticks, a restart every 19,200 ticks (6 sessions), tick p99 and managed heap after a full GC at
// every restart. LongSoak_GeneratedReference runs the same script on a generated world: the C0
// reference of the p99 gate. Set DREAM_SOAK_REFERENCE_P99_MS to that run's p99 to gate on it.
//
// The client-join legs need the stored-world join (workstream A, B); they are skipped until glue.
[Collection(TimingSensitiveCollection.Name)]
public sealed class StoredWorldSoakTests(ITestOutputHelper output)
{
    private const int ShortSize = 30;
    private const int ShortSessions = 3;
    private const int ShortSessionTicks = 3000;
    private const int LongSize = 100;
    private const int LongSessions = 6;
    private const int LongSessionTicks = 19_200;
    private const int LongSlotTicks = 128;

    [Fact]
    public void Short_StoredWorld_ThreeSessions_FractureAndRestart_KeepsEveryInvariant()
    {
        using StoredWorldDb db = new(ShortSize);
        RunSessions(db, ShortSessions, ShortSessionTicks, slotTicks: 64, out _, out _, out _);
    }

    // After each restart two clients join over loopback TCP and build their map from the terrain
    // transfer; checksum, raised cells and rubble equal the server's.
    [Fact]
    public async Task Short_StoredWorld_ClientJoinsAfterEachRestart_MatchTheServer()
    {
        using StoredWorldDb db = new(ShortSize);
        for (int session = 0; session < ShortSessions; session++)
        {
            using (SoakServer server = new(db.CreateHost()))
            {
                for (int tick = 0; tick < 1500; tick++)
                {
                    server.Step(tick);
                }
            }

            SqliteConnection.ClearAllPools();
            await using WireLoopbackServer wire = await WireLoopbackServer.StartAsync(db.CreateHost());
            await AssertClientsMatchAsync(wire, $"restart {session + 1}");
        }
    }

    [LoadFact]
    public void LongSoak_StoredWorld10k_115200Ticks_SixRestarts()
    {
        using StoredWorldDb db = new(LongSize);
        TickStats stats = RunSessions(db, LongSessions, LongSessionTicks, LongSlotTicks, out List<long> heaps, out int fractures, out int placements);
        output.WriteLine($"stored World10k soak: {stats.Describe()}; fractures {fractures}, placements {placements}");
        output.WriteLine($"managed heap after a full GC at each restart (MB): {string.Join(", ", heaps.Select(heap => (heap / 1048576d).ToString("0.0")))}");
        for (int k = 1; k < heaps.Count; k++)
        {
            Assert.True(heaps[k] <= heaps[0] * 1.10d, $"heap after restart {k + 1} ({heaps[k]:N0} B) exceeds restart 1 x 1.10 ({heaps[0]:N0} B)");
        }

        string? reference = Environment.GetEnvironmentVariable("DREAM_SOAK_REFERENCE_P99_MS");
        if (double.TryParse(reference, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double referenceP99))
        {
            Assert.True(stats.P99 <= referenceP99 * 1.10d, $"tick p99 {stats.P99:0.000} ms exceeds the generated reference {referenceP99:0.000} ms x 1.10");
        }
    }

    // The same script on a generated World10k (the host the stored soak is gated against).
    [LoadFact]
    public void LongSoak_GeneratedReference_115200Ticks_SixRestarts()
    {
        using StoredWorldDb db = new(LongSize, stored: false);
        TickStats stats = RunSessions(db, LongSessions, LongSessionTicks, LongSlotTicks, out List<long> heaps, out int fractures, out int placements, checkOverrides: false);
        output.WriteLine($"generated World10k soak: {stats.Describe()}; fractures {fractures}, placements {placements}");
        output.WriteLine($"managed heap after a full GC at each restart (MB): {string.Join(", ", heaps.Select(heap => (heap / 1048576d).ToString("0.0")))}");
    }

    [LoadFact]
    public async Task LongSoak_StoredWorld10k_TwoClientsJoinAfterEachRestart()
    {
        using StoredWorldDb db = new(LongSize);
        for (int session = 0; session < LongSessions; session++)
        {
            using (SoakServer server = new(db.CreateHost(), LongSlotTicks))
            {
                for (int tick = 0; tick < LongSessionTicks; tick++)
                {
                    server.Step(tick);
                }
            }

            SqliteConnection.ClearAllPools();
            await using WireLoopbackServer wire = await WireLoopbackServer.StartAsync(db.CreateHost());
            await AssertClientsMatchAsync(wire, $"restart {session + 1}");
        }
    }

    private TickStats RunSessions(
        StoredWorldDb db,
        int sessions,
        int ticksPerSession,
        int slotTicks,
        out List<long> heaps,
        out int fractures,
        out int placements,
        bool checkOverrides = true)
    {
        heaps = [];
        fractures = 0;
        placements = 0;
        Dictionary<string, int> rejections = [];
        List<double> tickMs = new(sessions * ticksPerSession);
        long counter = 0;
        for (int session = 0; session < sessions; session++)
        {
            // The previous session's host is disposed: the heap here is the steady state.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            heaps.Add(GC.GetTotalMemory(forceFullCollection: true));

            List<(string Category, string Message)> restoreLog = [];
            SoakServer server;
            using (Dream.Multiplayer.Diagnostics.NetworkDiagnosticsLog.CaptureOnCurrentThread((category, message) => restoreLog.Add((category, message))))
            {
                server = new SoakServer(db.CreateHost(), slotTicks);
            }

            using (server)
            {
                Assert.True(server.Runtime.PersistenceWrites?.Flush(TimeSpan.FromSeconds(60)) ?? true);
                string summary = checkOverrides
                    ? StoredWorldChecks.AssertConsistent(db, server.Runtime.Map, counter)
                    : "generated";
                if (checkOverrides)
                {
                    Assert.Equal(db.IsStored, server.Runtime.Map.Source is Dream.Simulation.World.Terrain.StoredMapSource);
                    counter = ObjectIdTestSupport.ReadIds(db.DatabasePath).NextObjectId;
                }

                // Rubble never goes orphan, and a restore heals nothing in a database this server wrote.
                Assert.DoesNotContain(restoreLog, line => line.Category == "server.persistence.orphan-rubble-pruned");
                Assert.DoesNotContain(restoreLog, line => line.Category == "server.persistence.terrain-override-healed");
                output.WriteLine($"session {session + 1} restored: {summary}");

                int startFractures = server.Fractures;
                for (int tick = 0; tick < ticksPerSession; tick++)
                {
                    long started = Stopwatch.GetTimestamp();
                    server.Step(tick);
                    tickMs.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }

                fractures += server.Fractures - startFractures;
                placements += server.Placements;
                foreach ((string reason, int count) in server.Rejections)
                {
                    rejections[reason] = rejections.GetValueOrDefault(reason) + count;
                }

                Assert.Empty(server.Runtime.Map.FindInvariantViolations());
                Assert.True(server.Runtime.PersistenceWrites?.Flush(TimeSpan.FromSeconds(60)) ?? true);
                output.WriteLine($"session {session + 1}: {ticksPerSession} ticks, {server.Fractures - startFractures} fractures, {server.Placements} placements so far, rubble cells {server.Runtime.Map.RubbleCells.Count}");
            }

            SqliteConnection.ClearAllPools();
        }

        // One last restart: the final state is consistent too.
        using (SoakServer last = new(db.CreateHost(), slotTicks))
        {
            Assert.True(last.Runtime.PersistenceWrites?.Flush(TimeSpan.FromSeconds(60)) ?? true);
            if (checkOverrides)
            {
                output.WriteLine("final restart: " + StoredWorldChecks.AssertConsistent(db, last.Runtime.Map, counter));
            }
        }

        SqliteConnection.ClearAllPools();
        Assert.True(fractures > 0, "the script never fractured anything");
        Assert.True(placements > 0, "the script never placed a cube; rejections: " + string.Join("; ", rejections.Select(pair => $"{pair.Value} x {pair.Key}")));
        TickStats stats = TickStats.From(tickMs);
        output.WriteLine($"{db.Size}x8x{db.Size} {(db.IsStored ? "stored" : "generated")}: {stats.Describe()}; {fractures} fractures, {placements} placements");
        return stats;
    }

    private static async Task AssertClientsMatchAsync(WireLoopbackServer wire, string label)
    {
        Map map = wire.Runtime.Map;
        List<WireLoopbackClient> clients = [];
        try
        {
            foreach (string name in new[] { "soak-client-1", "soak-client-2" })
            {
                clients.Add(await wire.ConnectAndJoinAsync(name));
            }

            foreach (WireLoopbackClient client in clients)
            {
                using ClientWorldSession world = new(map.Width, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
                TerrainPersistenceLoopbackTests.ApplyAllFrames(client, world);
                Assert.Equal(WorldStateChecksum.Compute(map), WorldStateChecksum.Compute(world.Map));
                Assert.Equal(
                    map.GeometryCells.Keys.Where(key => key.Y > 0).ToHashSet(),
                    world.Map.GeometryCells.Keys.Where(key => key.Y > 0).ToHashSet());
                Assert.Equal(map.RubbleCells.Keys.ToHashSet(), world.Map.RubbleCells.Keys.ToHashSet());
                Assert.True(label.Length > 0);
            }
        }
        finally
        {
            foreach (WireLoopbackClient client in clients)
            {
                await wire.DisconnectAsync(client);
            }
        }
    }

    internal sealed record TickStats(int Count, double Mean, double P50, double P99, double Max)
    {
        public static TickStats From(List<double> samples)
        {
            double[] sorted = [.. samples.Order()];
            return new TickStats(
                sorted.Length,
                sorted.Average(),
                sorted[sorted.Length / 2],
                sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.99))],
                sorted[^1]);
        }

        public string Describe() => $"{Count} ticks, mean {Mean:0.000} ms, p50 {P50:0.000}, p99 {P99:0.000}, max {Max:0.000}";
    }
}
