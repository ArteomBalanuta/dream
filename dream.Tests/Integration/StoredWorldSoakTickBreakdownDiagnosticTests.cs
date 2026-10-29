using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using Dream.Tests.Load;

namespace Dream.Tests.Integration;

// Persistence wave E tester, diagnostic for the long-soak p99 gap between the stored and the generated
// World10k (stored p99 about 22% higher, mean and p50 equal). SoakServer.Step times the script as well as
// the server: at the start of every slot (every 128 ticks, 0.8% of all ticks, so right at the p99 line)
// it runs Plan, which walks map.SolidCubes and sorts the walk goals. This splits the tick times into
// slot-start ticks and the rest, for both worlds, alternating.
[Collection(TimingSensitiveCollection.Name)]
public sealed class StoredWorldSoakTickBreakdownDiagnosticTests(ITestOutputHelper output)
{
    private const int Size = 100;
    private const int Slot = 128;
    private const int Sessions = 3;
    private const int Ticks = 19_200;

    [LoadFact]
    public void TickTimes_SplitBySlotStart_StoredAgainstGenerated()
    {
        for (int round = 0; round < 2; round++)
        {
            foreach (bool stored in round == 0 ? new[] { false, true } : new[] { true, false })
            {
                (List<double> slot, List<double> rest) = Run(stored);
                List<double> all = [.. slot, .. rest];
                double p99 = Percentile(all, 0.99);
                int slotAboveP99 = slot.Count(v => v >= p99);
                output.WriteLine(
                    $"{(stored ? "stored   " : "generated")} round {round + 1}: all p50 {Percentile(all, 0.5):0.000} p99 {p99:0.000} mean {all.Average():0.000} | " +
                    $"non-slot ({rest.Count}) p50 {Percentile(rest, 0.5):0.000} p99 {Percentile(rest, 0.99):0.000} p99.9 {Percentile(rest, 0.999):0.000} mean {rest.Average():0.000} | " +
                    $"slot-start ({slot.Count}) p50 {Percentile(slot, 0.5):0.000} p90 {Percentile(slot, 0.9):0.000} mean {slot.Average():0.000}; slot ticks at or above the overall p99: {slotAboveP99} of {all.Count(v => v >= p99)}");
            }
        }
    }

    // Where in the slot (tick mod 128) do the slowest 1% of the non-slot ticks fall? Both worlds.
    [LoadFact]
    public void SlowestOnePercent_ByPositionInTheSlot_StoredAgainstGenerated()
    {
        foreach (bool stored in new[] { false, true, false, true })
        {
            List<(int Tick, double Ms)> samples = [];
            using (StoredWorldDb db = new(Size, stored))
            {
                for (int session = 0; session < 2; session++)
                {
                    using (SoakServer server = new(db.CreateHost(), Slot))
                    {
                        GC.Collect();
                        for (int tick = 0; tick < Ticks; tick++)
                        {
                            long started = Stopwatch.GetTimestamp();
                            server.Step(tick);
                            samples.Add((tick, Stopwatch.GetElapsedTime(started).TotalMilliseconds));
                        }
                    }

                    SqliteConnection.ClearAllPools();
                }
            }

            double threshold = Percentile([.. samples.Select(sample => sample.Ms)], 0.99);
            int[] byResidue = new int[Slot];
            double[] sumByResidue = new double[Slot];
            int[] countByResidue = new int[Slot];
            foreach ((int tick, double ms) in samples)
            {
                countByResidue[tick % Slot]++;
                sumByResidue[tick % Slot] += ms;
                if (ms >= threshold)
                {
                    byResidue[tick % Slot]++;
                }
            }

            string top = string.Join(", ", Enumerable.Range(0, Slot).OrderByDescending(r => byResidue[r]).Take(8)
                .Select(r => $"{r}:{byResidue[r]}/{countByResidue[r]} (mean {sumByResidue[r] / countByResidue[r]:0.000})"));
            output.WriteLine($"{(stored ? "stored   " : "generated")} p99 {threshold:0.000}; ticks above it by tick mod {Slot} (residue: count/total): {top}");
        }
    }

    // The script's phase (walking toward a cube, attacking one, or idle) against the tick time.
    [LoadFact]
    public void TickTimes_ByScriptPhase_StoredAgainstGenerated()
    {
        System.Reflection.FieldInfo walkingField = typeof(SoakServer).GetField("_walking", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        System.Reflection.FieldInfo attackingField = typeof(SoakServer).GetField("_attacking", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        foreach (bool stored in new[] { false, true, false, true })
        {
            Dictionary<string, List<double>> byPhase = [];
            using (StoredWorldDb db = new(Size, stored))
            {
                for (int session = 0; session < 2; session++)
                {
                    using (SoakServer server = new(db.CreateHost(), Slot))
                    {
                        GC.Collect();
                        for (int tick = 0; tick < Ticks; tick++)
                        {
                            long started = Stopwatch.GetTimestamp();
                            server.Step(tick);
                            double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                            string phase = (bool)walkingField.GetValue(server)! ? "walking" : (bool)attackingField.GetValue(server)! ? "attacking" : "idle";
                            if (!byPhase.TryGetValue(phase, out List<double>? list))
                            {
                                byPhase[phase] = list = [];
                            }

                            list.Add(ms);
                        }
                    }

                    SqliteConnection.ClearAllPools();
                }
            }

            output.WriteLine($"{(stored ? "stored   " : "generated")}: " + string.Join(" | ", byPhase.OrderBy(pair => pair.Key).Select(pair =>
                $"{pair.Key} n={pair.Value.Count} p50 {Percentile(pair.Value, 0.5):0.000} p90 {Percentile(pair.Value, 0.9):0.000} p99 {Percentile(pair.Value, 0.99):0.000} mean {pair.Value.Average():0.000}")));
        }
    }

    // Is the slow tail periodic? The gaps between consecutive ticks above the p99 line, and the residues of
    // those ticks modulo a few candidate periods.
    [LoadFact]
    public void SlowTail_Periodicity_StoredAgainstGenerated()
    {
        foreach (bool stored in new[] { false, true })
        {
            List<double> ms = [];
            using (StoredWorldDb db = new(Size, stored))
            {
                using SoakServer server = new(db.CreateHost(), Slot);
                GC.Collect();
                for (int tick = 0; tick < Ticks; tick++)
                {
                    long started = Stopwatch.GetTimestamp();
                    server.Step(tick);
                    ms.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
            }

            SqliteConnection.ClearAllPools();
            double threshold = Percentile(ms, 0.99);
            List<int> slow = [.. Enumerable.Range(0, ms.Count).Where(i => ms[i] >= threshold)];
            string gaps = string.Join(", ", slow.Zip(slow.Skip(1), (a, b) => b - a).GroupBy(g => g).OrderByDescending(g => g.Count()).Take(8).Select(g => $"{g.Key}x{g.Count()}"));
            string periods = string.Join(" | ", new[] { 8, 16, 32, 64, 100, 128 }.Select(period =>
            {
                var top = slow.GroupBy(i => i % period).OrderByDescending(g => g.Count()).First();
                return $"mod {period}: residue {top.Key} holds {top.Count()}/{slow.Count}";
            }));
            output.WriteLine($"{(stored ? "stored   " : "generated")} p99 {threshold:0.000} ({slow.Count} ticks); most common gaps {gaps}; {periods}");
            output.WriteLine("  slowest-1% sample tick indices: " + string.Join(",", slow.Take(40)));
        }
    }

    // Tick time against the number of awake Bullet bodies after the tick (debris in flight).
    [LoadFact]
    public void TickTimes_ByAwakeBodyCount_StoredAgainstGenerated()
    {
        foreach (bool stored in new[] { false, true, false, true })
        {
            List<(int Awake, double Ms)> samples = [];
            List<(int Manifolds, int Pairs, int Objects)> physicsShape = [];
            using (StoredWorldDb db = new(Size, stored))
            {
                using SoakServer server = new(db.CreateHost(), Slot);
                GC.Collect();
                for (int tick = 0; tick < Ticks * 2; tick++)
                {
                    long started = Stopwatch.GetTimestamp();
                    server.Step(tick);
                    double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    var objects = server.Runtime.Physics.World.CollisionObjectArray;
                    int awake = 0;
                    for (int i = 0; i < objects.Count; i++)
                    {
                        if (objects[i].IsActive && !objects[i].IsStaticObject)
                        {
                            awake++;
                        }
                    }

                    samples.Add((awake, ms));
                    if (awake >= 31)
                    {
                        physicsShape.Add((server.Runtime.Physics.World.Dispatcher.NumManifolds, server.Runtime.Physics.World.Broadphase.OverlappingPairCache.NumOverlappingPairs, objects.Count));
                    }
                }
            }

            SqliteConnection.ClearAllPools();
            string table = string.Join(" | ", new (string Label, int Min, int Max)[] { ("0-2", 0, 2), ("3-10", 3, 10), ("11-30", 11, 30), ("31+", 31, int.MaxValue) }.Select(band =>
            {
                List<double> inBand = [.. samples.Where(sample => sample.Awake >= band.Min && sample.Awake <= band.Max).Select(sample => sample.Ms)];
                return inBand.Count == 0 ? $"awake {band.Label}: none" : $"awake {band.Label}: n={inBand.Count} p50 {Percentile(inBand, 0.5):0.000} p99 {Percentile(inBand, 0.99):0.000} mean {inBand.Average():0.000}";
            }));
            output.WriteLine($"{(stored ? "stored   " : "generated")}: {table}");
            output.WriteLine($"  ticks with 31+ awake: {physicsShape.Count}, mean contact manifolds {physicsShape.Average(x => x.Manifolds):0.0}, mean overlapping pairs {physicsShape.Average(x => x.Pairs):0.0}, collision objects {physicsShape.Average(x => x.Objects):0}");
        }
    }

    private static (List<double> SlotTicks, List<double> RestTicks) Run(bool stored)
    {
        using StoredWorldDb db = new(Size, stored);
        List<double> slot = [];
        List<double> rest = [];
        for (int session = 0; session < Sessions; session++)
        {
            using (SoakServer server = new(db.CreateHost(), Slot))
            {
                GC.Collect();
                for (int tick = 0; tick < Ticks; tick++)
                {
                    long started = Stopwatch.GetTimestamp();
                    server.Step(tick);
                    double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    (tick % Slot == 0 ? slot : rest).Add(ms);
                }

                Assert.True(server.Runtime.PersistenceWrites?.Flush(TimeSpan.FromSeconds(60)) ?? true);
            }

            SqliteConnection.ClearAllPools();
        }

        return (slot, rest);
    }

    private static double Percentile(List<double> values, double q)
    {
        double[] sorted = [.. values.Order()];
        return sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * q))];
    }
}
