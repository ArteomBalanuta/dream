using BulletSharp;
using BulletSharp.Math;
using Dream.Simulation.Determinism;
using Dream.Simulation.Physics;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Xunit;
using Xunit.Abstractions;
using TkVector3 = OpenTK.Mathematics.Vector3;

namespace Dream.Tests.Simulation.Physics;

// SCALE-5: PhysicsSyncSystem skips bodies that cannot have moved since the last sync (sleeping,
// not new, no rejection streak) when it is given the integrator's body set. Skipping must not
// change anything: the same world synced through a plain List (every body, the pre-SCALE-5 pass)
// must match it bit for bit on every tick.
public class PhysicsSyncActiveBodyTests
{
    private const float TickSeconds = 1f / 64f;

    private readonly ITestOutputHelper _output;

    public PhysicsSyncActiveBodyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // Builds, pushes, fractures, rubble removal, cube removal and long settles, on the enriched
    // startup terrain: some cubes rest on partial terrain cells and cycle through the sync's
    // reject/reset path, which exercises the rejection streaks. Compared every tick: moved-cube
    // deltas (the mutation export), the simulation checksum, and every map body's transform,
    // activation state and stored center.
    [Fact]
    public void ScriptedWorld_SkippingSleepingBodies_MatchesSyncingEveryBodyEveryTick()
    {
        using ScriptedWorld skipping = new();
        using ScriptedWorld everyBody = new();
        long skipped = 0;
        long synced = 0;
        int movedTotal = 0;
        for (int tick = 0; tick < 1_100; tick++)
        {
            skipping.ApplyScript(tick);
            everyBody.ApplyScript(tick);

            skipping.Physics.StepSimulationFixed(TickSeconds);
            everyBody.Physics.StepSimulationFixed(TickSeconds);

            PhysicsSyncResult skippingResult = skipping.Sync.SyncDynamicMapBodies(skipping.Map, skipping.Physics.DynamicMapCubeBodies);
            PhysicsSyncResult everyBodyResult = everyBody.Sync.SyncDynamicMapBodies(everyBody.Map, everyBody.Physics.DynamicMapCubeBodies.ToList());
            skipped += skipping.Sync.LastSkippedBodyCount;
            synced += skipping.Sync.LastSyncedBodyCount;
            Assert.Equal(0, everyBody.Sync.LastSkippedBodyCount);

            Assert.Equal(Describe(everyBodyResult), Describe(skippingResult));
            movedTotal += skippingResult.MovedCubes.Count;
            Assert.Equal(
                SimulationStateChecksum.Compute(everyBody.Map, [], 0),
                SimulationStateChecksum.Compute(skipping.Map, [], 0));
            Assert.Equal(everyBody.Sync.TrackedCubeBaselineCount, skipping.Sync.TrackedCubeBaselineCount);
            AssertSameBodies(everyBody.Physics, skipping.Physics, tick);
        }

        _output.WriteLine($"synced={synced} skipped={skipped} moved={movedTotal}");
        Assert.True(movedTotal > 50, $"the script should move cubes, moved {movedTotal}");
        // The comparison only means something if the skipping path did skip: rubble and the cubes
        // cycling on partial terrain stay awake, the spaced carpets sleep between events.
        Assert.True(skipped > 10_000, $"the sync should skip sleeping bodies: synced={synced} skipped={skipped}");
    }

    // Sub-millimetre creep (wave-5 fix B) is still reported once the accumulated drift passes the
    // threshold while every other body sleeps and is skipped.
    [Fact]
    public void CreepingCube_AmongSleepingCubes_IsStillReportedOnceDriftAccumulates()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, width: 16, depth: 16);
        for (int x = 1; x < 15; x += 2)
        {
            for (int z = 1; z < 15; z += 2)
            {
                map.AddCube(new TkVector3(x, 1f, z), withPhysics: true);
            }
        }

        PhysicsSyncSystem sync = new();
        sync.SyncDynamicMapBodies(map, physics.DynamicMapCubeBodies);
        ForceAllAsleep(physics);
        sync.SyncDynamicMapBodies(map, physics.DynamicMapCubeBodies);
        sync.SyncDynamicMapBodies(map, physics.DynamicMapCubeBodies);
        Assert.Equal(0, sync.LastSyncedBodyCount);

        RigidBody creeper = physics.DynamicMapCubeBodies[5];
        Cube cube = ((ValueTuple<Cube, TkVector3>)creeper.UserObject).Item1;
        TkVector3 start = cube.Position;
        int reports = 0;
        TkVector3 lastReported = start;
        for (int step = 0; step < 40; step++)
        {
            // 0.2 mm per tick, along x: below the 1 mm report threshold every single tick.
            TkVector3 next = start + new TkVector3(0.0002f * (step + 1), 0f, 0f);
            Matrix transform = Matrix.Translation(next.X, next.Y, next.Z);
            creeper.WorldTransform = transform;
            creeper.MotionState.SetWorldTransform(ref transform);
            creeper.Activate(true);

            PhysicsSyncResult result = sync.SyncDynamicMapBodies(map, physics.DynamicMapCubeBodies);
            Assert.Equal(1, sync.LastSyncedBodyCount);
            foreach (PhysicsCubeMoveDelta moved in result.MovedCubes)
            {
                Assert.Same(cube, moved.Cube);
                Assert.True((moved.CenterPosition - lastReported).Length > 0.001f);
                lastReported = moved.CenterPosition;
                reports++;
            }

            creeper.ForceActivationState(ActivationState.IslandSleeping);
        }

        Assert.InRange(reports, 5, 8);
    }

    // The work a sync does is proportional to the bodies that can have moved: with 10k sleeping
    // cube bodies and 10 awake ones, it processes those 10 (plus nothing else) and skips the rest.
    [Fact]
    public void TenThousandSleepingAndTenActive_SyncProcessesOnlyTheActiveTen()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, width: 102, height: 4, depth: 102);
        for (int x = 1; x <= 100; x++)
        {
            for (int z = 1; z <= 100; z++)
            {
                Assert.NotNull(map.TryAddCube(new TkVector3(x, 1f, z), withPhysics: true));
            }
        }

        Assert.Equal(10_000, physics.DynamicMapCubeBodies.Count);
        PhysicsSyncSystem sync = new();
        sync.SyncDynamicMapBodies(map, physics.DynamicMapCubeBodies);
        Assert.Equal(10_000, sync.LastSyncedBodyCount);

        ForceAllAsleep(physics);
        sync.SyncDynamicMapBodies(map, physics.DynamicMapCubeBodies);
        sync.SyncDynamicMapBodies(map, physics.DynamicMapCubeBodies);
        Assert.Equal(0, sync.LastSyncedBodyCount);
        Assert.Equal(10_000, sync.LastSkippedBodyCount);

        RigidBody[] awake = Enumerable.Range(0, 10).Select(i => physics.DynamicMapCubeBodies[i * 997]).ToArray();
        foreach (RigidBody body in awake)
        {
            body.Activate(true);
        }

        sync.SyncDynamicMapBodies(map, physics.DynamicMapCubeBodies);
        Assert.Equal(10, sync.LastSyncedBodyCount);
        Assert.Equal(9_990, sync.LastSkippedBodyCount);
        Assert.Equal(10_000, sync.TrackedCubeBaselineCount);

        // A removal prunes exactly the removed cube's baseline, as the full pass would.
        Cube removed = ((ValueTuple<Cube, TkVector3>)physics.DynamicMapCubeBodies[4242].UserObject).Item1;
        Assert.True(map.RemoveCube(removed));
        sync.SyncDynamicMapBodies(map, physics.DynamicMapCubeBodies);
        Assert.Equal(9_999, sync.TrackedCubeBaselineCount);
    }

    private static void ForceAllAsleep(PhysicsIntegrator physics)
    {
        foreach (RigidBody body in physics.DynamicMapCubeBodies)
        {
            body.ForceActivationState(ActivationState.IslandSleeping);
        }
    }

    private static string Describe(PhysicsSyncResult result)
    {
        return string.Join(
            "; ",
            result.MovedCubes.Select(moved =>
                $"{moved.SourceCellPosition}->{moved.TargetCellPosition}@{Bits(moved.CenterPosition)}"));
    }

    private static string Bits(TkVector3 value)
    {
        return $"{BitConverter.SingleToInt32Bits(value.X):x8}{BitConverter.SingleToInt32Bits(value.Y):x8}{BitConverter.SingleToInt32Bits(value.Z):x8}";
    }

    private static void AssertSameBodies(PhysicsIntegrator expected, PhysicsIntegrator actual, int tick)
    {
        Assert.Equal(expected.MapCubeBodies.Count, actual.MapCubeBodies.Count);
        Assert.Equal(expected.DynamicMapCubeBodies.Count, actual.DynamicMapCubeBodies.Count);
        using InsertionOrderedSet<RigidBody>.Enumerator expectedBodies = expected.MapCubeBodies.GetEnumerator();
        using InsertionOrderedSet<RigidBody>.Enumerator actualBodies = actual.MapCubeBodies.GetEnumerator();
        while (expectedBodies.MoveNext())
        {
            Assert.True(actualBodies.MoveNext());
            RigidBody left = expectedBodies.Current;
            RigidBody right = actualBodies.Current;
            left.MotionState.GetWorldTransform(out Matrix leftTransform);
            right.MotionState.GetWorldTransform(out Matrix rightTransform);
            string where = $"tick {tick}";
            Assert.True(
                Bits(leftTransform.Origin.ToOpenTK()) == Bits(rightTransform.Origin.ToOpenTK()),
                $"{where}: transform {leftTransform.Origin} vs {rightTransform.Origin}");
            Assert.True(
                Bits(left.LinearVelocity.ToOpenTK()) == Bits(right.LinearVelocity.ToOpenTK()),
                $"{where}: velocity");
            Assert.Equal(left.ActivationState, right.ActivationState);
            Assert.Equal(StoredCenter(left), StoredCenter(right));
        }
    }

    private static string StoredCenter(RigidBody body)
    {
        return body.UserObject switch
        {
            ValueTuple<Cube, TkVector3> cubeBody => "cube " + Bits(cubeBody.Item2),
            GeometryDynamicBodyReference piece => $"piece {piece.CellKey} {piece.PieceIndex} {Bits(piece.InitialCenter)}",
            null => "none",
            _ => body.UserObject.GetType().Name
        };
    }

    private sealed class ScriptedWorld : IDisposable
    {
        public ScriptedWorld()
        {
            Physics = new PhysicsIntegrator();
            Map = new Map(Physics, generateStartupTerrain: true, width: 56, depth: 56);
            // A carpet over the mound east of the pyramid (partial terrain cells: reject/reset
            // cycles) and a flat carpet in the open.
            // Every other column, so contact islands stay small and most of them sleep between
            // script events; a row along z = 11 is contiguous.
            for (int x = 24; x <= 31; x++)
            {
                for (int z = 11; z <= 18; z += x % 2 == 0 ? 1 : 2)
                {
                    AddOnTop(x, z);
                }
            }

            for (int x = 4; x <= 11; x += 2)
            {
                for (int z = 26; z <= 33; z += 2)
                {
                    AddOnTop(x, z);
                }
            }
        }

        public PhysicsIntegrator Physics { get; }

        public Map Map { get; }

        public PhysicsSyncSystem Sync { get; } = new();

        public void ApplyScript(int tick)
        {
            if (tick % 37 == 5)
            {
                // A build on the flat carpet, stacking.
                AddOnTop(4 + (tick / 37 % 4 * 2), 26 + (tick / 37 * 3 % 4 * 2));
            }

            if (tick % 89 == 11 && Physics.DynamicMapCubeBodies.Count > 0)
            {
                // A push, as a player or an explosion would give.
                RigidBody body = Physics.DynamicMapCubeBodies[(tick * 7919) % Physics.DynamicMapCubeBodies.Count];
                body.LinearVelocity = new BulletSharp.Math.Vector3(tick % 2 == 0 ? 2.5f : -2.5f, 1f, 0.5f);
                body.Activate(true);
            }

            if (tick % 53 == 17)
            {
                Cube? cube = PickPlacedCube(tick);
                if (cube != null && Map.TryGetCellForCube(cube, out GeometryCellKey cell))
                {
                    Map.FractureCubeIntoPieces(cell);
                }
            }

            if (tick % 71 == 23)
            {
                Cube? cube = PickPlacedCube(tick * 3);
                if (cube != null)
                {
                    Map.RemoveCube(cube);
                }
            }

            if (tick % 97 == 41)
            {
                var pieces = Map.CaptureFracturePieces();
                if (pieces.Count > 0)
                {
                    Map.RemoveRubble(pieces[(tick / 97) % pieces.Count].Cell);
                }
            }
        }

        public void Dispose()
        {
            Physics.Dispose();
        }

        private Cube? PickPlacedCube(int salt)
        {
            List<Cube> placed = Map.SolidCubes
                .Where(cube => Map.TryGetCellForCube(cube, out GeometryCellKey key) && key.Y > 0 && Map.TryGetReplicatedId(cube, out _) == false && cube.Position.Y > 1f)
                .ToList();
            return placed.Count == 0 ? null : placed[(salt * 31) % placed.Count];
        }

        private void AddOnTop(int x, int z)
        {
            for (int y = 1; y < Map.Height - 1; y++)
            {
                if (Map.Cubes[x, y, z] == null)
                {
                    Map.TryAddCube(new TkVector3(x, y, z), withPhysics: true);
                    return;
                }
            }
        }
    }
}
