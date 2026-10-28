using System.Runtime.CompilerServices;
using Dream.Simulation.Determinism;
using Dream.Simulation.Physics;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Xunit;
using Xunit.Abstractions;
using Vector3 = OpenTK.Mathematics.Vector3;

namespace Dream.Tests.Load;

// Wave 12 tester: the SCALE-7a implementer saw a full checksum walk over Map's read-only
// dictionaries (IReadOnlyDictionary.Values) return a wrong sum in Release, only after a load test
// had run in the same process, and suspected a tiered-PGO miscompile. This tries to reproduce it:
// after the HeadlessMap load test has warmed the process (DREAM_W12_SKIP_PRELOAD=1 skips it), it
// walks a World10k-sized map hundreds of times, through every enumerator pattern the old and
// current code used, while the map is edited between rounds, and compares each walk with a sum
// over a plain array copy of the same records. SimulationStateChecksum (which also walks
// RubbleCells.Values through the interface) has no independent reference, so its values go into a
// digest that must not change between DOTNET_TieredPGO=1, DOTNET_TieredPGO=0 and
// DOTNET_TieredCompilation=0 runs (the map edits are deterministic). Run in Release with
// DREAM_LOAD_TEST=1. Each walk is its own non-inlined method, so tiering promotes it on its own.
[Collection(TimingSensitiveCollection.Name)]
public sealed class Wave12ChecksumWalkJitTests(ITestOutputHelper output)
{
    private const int Rounds = 600;

    [LoadFact]
    public async Task ChecksumWalks_AfterALoadTest_MatchAnArraySum_EveryRound()
    {
        // The load test the wrong sum was seen after. DREAM_W12_PRELOAD=world10k runs the real-server
        // World10k scenario first (30 s; its server computes the world checksum on the tick).
        if (Environment.GetEnvironmentVariable("DREAM_W12_SKIP_PRELOAD") != "1")
        {
            new HeadlessMapMeasurementTests(output).World10k_HeadlessVersusRenderingMap();
        }

        if (Environment.GetEnvironmentVariable("DREAM_W12_PRELOAD") == "world10k")
        {
            await new ServerLoadTests(output).World10k();
        }

        output.WriteLine(
            $"DOTNET_TieredPGO={Environment.GetEnvironmentVariable("DOTNET_TieredPGO") ?? "(default)"}, " +
            $"DOTNET_TieredCompilation={Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "(default)"}");
        using PhysicsIntegrator physics = new();
        Map map = new(physics, generateStartupTerrain: true, width: 100, depth: 100, replicatedCubeBodies: true, headless: true);
        Random random = new(12_012);
        List<GeometryCellKey> placed = [];
        while (placed.Count < 10_000)
        {
            Vector3 cell = new(random.Next(100), 1 + random.Next(map.Height - 2), random.Next(100));
            if (map.AddCube(cell, isStatic: false, withPhysics: true, GeometryMaterialId.Stone))
            {
                placed.Add(new GeometryCellKey((int)cell.X, (int)cell.Y, (int)cell.Z));
            }
        }

        int fractured = 0;
        int mismatches = 0;
        ulong digest = 0xCBF29CE484222325UL;
        List<string> firstMismatches = [];
        for (int round = 0; round < Rounds; round++)
        {
            // Edit between rounds: two fractures (geometry cells become rubble cells).
            for (int f = 0; f < 2 && fractured < placed.Count; f++)
            {
                map.FractureCubeIntoPieces(placed[fractured++]);
            }

            ulong reference = ArraySum(map);
            (string Name, ulong Value)[] walks =
            [
                ("running (Map.WorldChecksum)", WorldStateChecksum.Compute(map)),
                ("Map.ComputeWorldChecksumFull (concrete Dictionary)", WorldStateChecksum.ComputeFull(map)),
                ("IReadOnlyDictionary.Values foreach x2 (the walk the implementer saw fail)", InterfaceValuesWalk(map)),
                ("Values.Concat LINQ (pre-SCALE-7 Compute)", ConcatWalk(map)),
            ];
            foreach ((string name, ulong value) in walks)
            {
                if (value != reference)
                {
                    mismatches++;
                    if (firstMismatches.Count < 10)
                    {
                        firstMismatches.Add($"round {round}: {name} = {value:X16}, array sum {reference:X16}");
                    }
                }
            }

            SimulationStateChecksum.Parts parts = SimulationStateChecksum.ComputeParts(map, [], 0);
            digest = unchecked((digest ^ parts.Rubble) * 0x100000001B3UL);
            digest = unchecked((digest ^ parts.Cubes) * 0x100000001B3UL);
            digest = unchecked((digest ^ reference) * 0x100000001B3UL);

            if (round % 50 == 0)
            {
                // Give the background tier-up a chance to install the rejitted walks.
                Thread.Sleep(50);
            }
        }

        output.WriteLine($"{Rounds} rounds, {map.GeometryCells.Count} cells, {map.RubbleCells.Count} rubble cells: {mismatches} mismatches; SimulationStateChecksum digest {digest:X16}");
        foreach (string line in firstMismatches)
        {
            output.WriteLine(line);
        }

        Assert.Equal(0, mismatches);
    }

    private static ulong ArraySum(Map map)
    {
        GeometryCellRecord[] cells = [.. map.GeometryCells.Values, .. map.RubbleCells.Values];
        ulong sum = 0;
        ulong count = 0;
        for (int i = 0; i < cells.Length; i++)
        {
            if (WorldStateChecksum.TryHashCell(cells[i], out ulong hash))
            {
                sum = unchecked(sum + hash);
                count++;
            }
        }

        return WorldStateChecksum.Finish(sum, count);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong InterfaceValuesWalk(Map map)
    {
        ulong sum = 0;
        ulong count = 0;
        foreach (GeometryCellRecord record in map.GeometryCells.Values)
        {
            if (WorldStateChecksum.TryHashCell(record, out ulong hash))
            {
                sum = unchecked(sum + hash);
                count++;
            }
        }

        foreach (GeometryCellRecord record in map.RubbleCells.Values)
        {
            if (WorldStateChecksum.TryHashCell(record, out ulong hash))
            {
                sum = unchecked(sum + hash);
                count++;
            }
        }

        return WorldStateChecksum.Finish(sum, count);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong ConcatWalk(Map map)
    {
        ulong sum = 0;
        ulong count = 0;
        foreach (GeometryCellRecord record in map.GeometryCells.Values.Concat(map.RubbleCells.Values))
        {
            if (WorldStateChecksum.TryHashCell(record, out ulong hash))
            {
                sum = unchecked(sum + hash);
                count++;
            }
        }

        return WorldStateChecksum.Finish(sum, count);
    }
}
