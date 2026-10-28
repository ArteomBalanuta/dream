using System.Diagnostics;
using Dream.Simulation.Physics;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Vector3 = OpenTK.Mathematics.Vector3;

namespace Dream.Tests.Load;

// SCALE-7(c)/(d) measurement (DREAM_LOAD_TEST=1, Release) on a World10k-sized map (100x100 floor
// with the startup terrain, 10k dynamic placed cubes, 1k rubble cells, Bullet bodies as on the
// server): a rendering map against a headless one. Reports the build time (startup), the managed
// bytes the map retains after a full GC, the cost of a fracture (milliseconds and bytes) and of a
// cube moving across cells, and the world checksum as a full walk (before SCALE-7(d)) against the
// running value (after).
[Collection(TimingSensitiveCollection.Name)]
public sealed class HeadlessMapMeasurementTests
{
    private const int FloorSize = 100;
    private const int Cubes = 10_000;
    private const int RubbleCells = 1_000;
    private const int Fractures = 500;
    private const int Moves = 5_000;
    private const int ChecksumRounds = 200;

    private readonly ITestOutputHelper _output;

    public HeadlessMapMeasurementTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [LoadFact]
    public void World10k_HeadlessVersusRenderingMap()
    {
        // Warm-up pass so JIT tiering does not land on the first measured map.
        Run(headless: false, report: false);
        Run(headless: true, report: false);

        _output.WriteLine($"World10k-sized map: {FloorSize}x{FloorSize} startup terrain, {Cubes} placed dynamic cubes, {RubbleCells} rubble cells, with physics");
        _output.WriteLine("| Map | Build ms | Retained managed MB | Fracture ms mean / p99 | Fracture bytes | Cell-change move us mean | Move bytes |");
        _output.WriteLine("| --- | --- | --- | --- | --- | --- | --- |");
        MapRun rendering = Run(headless: false, report: true);
        MapRun headless = Run(headless: true, report: true);

        Assert.Equal(rendering.Checksum, headless.Checksum);
        Assert.True(headless.RetainedBytes < rendering.RetainedBytes);
    }

    private MapRun Run(bool headless, bool report)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long before = GC.GetTotalMemory(forceFullCollection: true);

        using PhysicsIntegrator physics = new();
        Stopwatch build = Stopwatch.StartNew();
        Map map = new(physics, generateStartupTerrain: true, width: FloorSize, depth: FloorSize, replicatedCubeBodies: true, headless: headless);
        List<GeometryCellKey> placed = [];
        Random random = new(10_000);
        while (placed.Count < Cubes)
        {
            Vector3 cell = new(random.Next(FloorSize), 1 + random.Next(map.Height - 2), random.Next(FloorSize));
            if (map.AddCube(cell, isStatic: false, withPhysics: true, GeometryMaterialId.Stone))
            {
                placed.Add(new GeometryCellKey((int)cell.X, (int)cell.Y, (int)cell.Z));
            }
        }

        int rubble = 0;
        while (rubble < RubbleCells)
        {
            GeometryCellKey cell = new(random.Next(FloorSize), 1 + random.Next(map.Height - 2), random.Next(FloorSize));
            if (map.RestoreFracturePieces(cell, GeometryMaterialId.Sand, CubeFracturePattern.CreateFirstPassRubble(cell)))
            {
                rubble++;
            }
        }

        build.Stop();
        long retained = GC.GetTotalMemory(forceFullCollection: true) - before;

        // Fracture: a placed cube becomes rubble (what a fatal hit does on the tick).
        double[] fractureMs = new double[Fractures];
        long fractureBytes = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Fractures; i++)
        {
            long start = Stopwatch.GetTimestamp();
            Assert.True(map.FractureCubeIntoPieces(placed[i]));
            fractureMs[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        fractureBytes = (GC.GetAllocatedBytesForCurrentThread() - fractureBytes) / Fractures;

        // Cell-change moves, synced, as the physics sync does for a falling cube.
        long moveBytes = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch moves = Stopwatch.StartNew();
        int moved = 0;
        for (int i = 0; i < Moves; i++)
        {
            GeometryCellKey key = placed[Fractures + (i % (placed.Count - Fractures))];
            if (map.TryGetCubeAtCell(key.X, key.Y, key.Z) is not Cube cube)
            {
                continue;
            }

            Vector3 target = cube.Position + new Vector3(0f, 0f, (i & 1) == 0 ? 1f : -1f);
            if (map.UpdateCubePosition(cube, target, reservedTargetCells: null, syncPhysics: true))
            {
                placed[Fractures + (i % (placed.Count - Fractures))] = new GeometryCellKey(key.X, key.Y, key.Z + ((i & 1) == 0 ? 1 : -1));
                moved++;
            }
        }

        moves.Stop();
        moveBytes = (GC.GetAllocatedBytesForCurrentThread() - moveBytes) / Math.Max(1, moved);

        // Checksum: full walk (every checksum before SCALE-7(d)) against the running value.
        ulong sink = 0;
        long fullStart = Stopwatch.GetTimestamp();
        for (int i = 0; i < ChecksumRounds; i++)
        {
            sink ^= WorldStateChecksum.ComputeFull(map);
        }

        double fullUs = Stopwatch.GetElapsedTime(fullStart).TotalMicroseconds / ChecksumRounds;
        long runningStart = Stopwatch.GetTimestamp();
        for (int i = 0; i < ChecksumRounds; i++)
        {
            sink ^= WorldStateChecksum.Compute(map);
        }

        double runningUs = Stopwatch.GetElapsedTime(runningStart).TotalMicroseconds / ChecksumRounds;
        Assert.Equal(WorldStateChecksum.ComputeFull(map), WorldStateChecksum.Compute(map));

        Array.Sort(fractureMs);
        if (report)
        {
            _output.WriteLine(
                $"| {(headless ? "headless" : "rendering")} | {build.Elapsed.TotalMilliseconds:0} | {retained / 1048576d:0.0} | " +
                $"{fractureMs.Average():0.000} / {fractureMs[(int)(Fractures * 0.99)]:0.000} | {fractureBytes} | " +
                $"{moves.Elapsed.TotalMicroseconds / Math.Max(1, moved):0.0} ({moved} moved) | {moveBytes} |");
            _output.WriteLine(
                $"  world checksum ({map.GeometryCells.Count} cells, {map.RubbleCells.Count} rubble): full walk {fullUs:0.0} us, running {runningUs:0.000} us (sink {sink & 1})");
        }

        GC.KeepAlive(map);
        return new MapRun(retained, WorldStateChecksum.Compute(map));
    }

    private readonly record struct MapRun(long RetainedBytes, ulong Checksum);
}
