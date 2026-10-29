using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;
using Dream.Simulation.Physics;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Simulation.World.MapSources;
using Dream.Simulation.World.Terrain;

namespace Dream.Tests.Load;

// Persistence wave E tester, diagnostic for the long-soak p99 gap (stored World10k tick p99 about 22%
// above the generated one, mean and p50 equal). A stored map hands its cells to the map in (x, z, y)
// order, the generator in its own order; the cells are the same, only the order the static cube bodies
// enter Bullet's broadphase differs. This builds the same map four ways, fractures the same cells, and
// times the physics step while the debris falls and settles:
//   generated            the generator's order
//   generated-xzy        the same generated cells re-sorted to (x, z, y)         (no stored code involved)
//   stored               StoredMapSource, (x, z, y)
//   stored-generator     the stored cells re-ordered to the generator's order
// If the p99 follows the order and not the source, the order is the cause.
public sealed class StoredMapPhysicsOrderDiagnosticTests(ITestOutputHelper output)
{
    private const int Size = 100;
    private const int Rounds = 7;
    private const int Steps = 1200;

    [LoadFact]
    public void DebrisStepCost_FollowsTheBodyInsertionOrder_NotTheMapSource()
    {
        MapDefinition definition = new(Size, 8, Size, WorldMapSource.Generated, WorldGeneratorCatalog.DefaultKey, WorldGeneratorCatalog.DefaultVersion);
        GeneratedMapSource generated = new(definition);
        IReadOnlyList<Dream.Simulation.Persistence.PersistedTerrainChunk> chunks = TerrainChunkExporter.Export(generated);
        StoredMapSource stored = new(definition, chunks);
        List<GeometryCellKey> generatorOrder = [.. generated.CreateCells().Select(cell => cell.Key)];
        Dictionary<GeometryCellKey, int> rank = generatorOrder.Select((key, index) => (key, index)).ToDictionary(pair => pair.key, pair => pair.index);

        (string Name, Func<IMapSource> Create)[] variants =
        [
            ("generated", () => generated),
            ("generated-xzy", () => new Reordered(generated, cells => [.. cells.OrderBy(c => (c.Key.X, c.Key.Z, c.Key.Y))])),
            ("stored", () => stored),
            ("stored-generator", () => new Reordered(stored, cells => [.. cells.OrderBy(c => rank[c.Key])]))
        ];

        Dictionary<string, List<Stats>> results = variants.ToDictionary(v => v.Name, _ => new List<Stats>());
        for (int round = 0; round < Rounds + 1; round++)
        {
            foreach ((string name, Func<IMapSource> create) in round % 2 == 0 ? variants : variants.Reverse())
            {
                Stats stats = Run(create());
                if (round > 0)
                {
                    results[name].Add(stats);
                }
            }
        }

        foreach ((string name, _) in variants)
        {
            List<Stats> list = results[name];
            output.WriteLine($"{name,-18} step mean {list.Average(s => s.Mean):0.000} ms  p50 {list.Average(s => s.P50):0.000}  p99 {list.Average(s => s.P99):0.000} (min {list.Min(s => s.P99):0.000}, max {list.Max(s => s.P99):0.000})  p99.9 {list.Average(s => s.P999):0.000}  steps with 31+ awake bodies {list.Average(s => s.BusySteps):0} (p50 {list.Average(s => s.BusyP50):0.000} ms)");
        }
    }

    private sealed record Stats(double Mean, double P50, double P99, double P999, int BusySteps, double BusyP50);

    private static Stats Run(IMapSource source)
    {
        using PhysicsIntegrator physics = new();
        Map map = new(source, physics, replicatedCubeBodies: false, headless: true);
        // The same cells in every variant: standing raised source cubes, in (x, z, y) order, every 7th.
        // Clustered, nearest the middle of the world first, so the debris piles up the way it does at a
        // player's feet.
        List<GeometryCellKey> targets = [.. map.GeometryCells.Keys
            .Where(key => key.Y > 0 && map.IsSourceCell(key) && map.Cubes[key.X, key.Y, key.Z] != null)
            .OrderBy(key => ((key.X - 50) * (key.X - 50)) + ((key.Z - 50) * (key.Z - 50)) + (key.Y * 1000))
            .ThenBy(key => (key.X, key.Z, key.Y))
            .Take(400)];
        double[] times = new double[Steps];
        int[] awakeCounts = new int[Steps];
        int next = 0;
        for (int step = 0; step < Steps; step++)
        {
            if (step % 96 == 0)
            {
                for (int i = 0; i < 8 && next < targets.Count; i++, next++)
                {
                    map.FractureCubeIntoPieces(targets[next]);
                }
            }

            long started = Stopwatch.GetTimestamp();
            physics.StepSimulationFixed(1f / 64f);
            times[step] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var objects = physics.World.CollisionObjectArray;
            int awake = 0;
            for (int i = 0; i < objects.Count; i++)
            {
                if (objects[i].IsActive && !objects[i].IsStaticObject)
                {
                    awake++;
                }
            }

            awakeCounts[step] = awake;
        }

        double[] sorted = [.. times.Order()];
        double[] busy = [.. Enumerable.Range(0, Steps).Where(i => awakeCounts[i] >= 31).Select(i => times[i]).Order()];
        return new Stats(
            times.Average(),
            sorted[Steps / 2],
            sorted[(int)(Steps * 0.99)],
            sorted[(int)(Steps * 0.999)],
            busy.Length,
            busy.Length == 0 ? 0 : busy[busy.Length / 2]);
    }

    private sealed class Reordered(IMapSource inner, Func<IReadOnlyList<GeometryCellRecord>, IReadOnlyList<GeometryCellRecord>> order) : IMapSource
    {
        public MapDefinition Definition => inner.Definition;

        public IReadOnlyList<GeometryCellRecord> CreateCells() => order(inner.CreateCells());
    }
}
