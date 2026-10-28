using System.Diagnostics;
using BulletSharp;
using Dream.Simulation.Physics;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Simulation.World;

// SCALE-5: removing a cube's bodies goes through reverse indexes (cube -> bodies, body -> cell)
// instead of scanning every map body and every cell list. The removed set and the order of
// everything that remains must be exactly what the scan produced.
public class WorldPhysicsBinderRemovalTests
{
    private readonly ITestOutputHelper _output;

    public WorldPhysicsBinderRemovalTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void RemoveCube_RemovesExactlyTheScannedBodies_AndKeepsTheRestInOrder()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, generateStartupTerrain: true, width: 32, depth: 32);
        Random random = new(11);
        for (int i = 0; i < 300; i++)
        {
            int x = 2 + random.Next(28);
            int z = 2 + random.Next(28);
            for (int y = 1; y < map.Height - 1; y++)
            {
                if (map.Cubes[x, y, z] == null)
                {
                    map.TryAddCube(new Vector3(x, y, z), withPhysics: true);
                    break;
                }
            }
        }

        // Moves through the sync reindex bodies between cells; fractures add rubble bodies.
        PhysicsSyncSystem sync = new();
        for (int tick = 0; tick < 90; tick++)
        {
            physics.StepSimulationFixed(1f / 64f);
            sync.SyncDynamicMapBodies(map, physics.DynamicMapCubeBodies);
        }

        List<Cube> placed = map.SolidCubes.Where(cube => cube.Position.Y > 1f).ToList();
        for (int i = 0; i < 6; i++)
        {
            Assert.True(map.TryGetCellForCube(placed[i * 7], out GeometryCellKey cell));
            map.FractureCubeIntoPieces(cell);
        }

        placed = map.SolidCubes.Where(cube => cube.Position.Y > 1f).ToList();
        int removedBodies = 0;
        for (int i = 0; i < 60; i++)
        {
            Cube cube = placed[(i * 37) % placed.Count];
            if (!map.TryGetCellForCube(cube, out _))
            {
                continue;
            }

            List<RigidBody> before = physics.MapCubeBodies.ToList();
            List<RigidBody> beforeDynamic = physics.DynamicMapCubeBodies.ToList();
            HashSet<RigidBody> expectedRemoved = before
                .Where(body => body.UserObject is ValueTuple<Cube, Vector3> cubeBody && ReferenceEquals(cubeBody.Item1, cube))
                .ToHashSet();

            Assert.True(map.RemoveCube(cube));

            Assert.Equal(before.Where(body => !expectedRemoved.Contains(body)).ToList(), physics.MapCubeBodies.ToList());
            Assert.Equal(beforeDynamic.Where(body => !expectedRemoved.Contains(body)).ToList(), physics.DynamicMapCubeBodies.ToList());
            removedBodies += expectedRemoved.Count;
            Assert.Empty(map.FindInvariantViolations());
        }

        Assert.True(removedBodies >= 40, $"removed {removedBodies} bodies");

        // The indexes stay usable: the world keeps simulating and syncing cleanly.
        for (int tick = 0; tick < 60; tick++)
        {
            physics.StepSimulationFixed(1f / 64f);
            sync.SyncDynamicMapBodies(map, physics.DynamicMapCubeBodies);
        }

        Assert.Empty(map.FindInvariantViolations());
    }

    // Removing a cube used to scan every map body and every cell's body list, so its cost grew
    // with the world. The managed part is now O(the cube's bodies). Bullet's own removal is not:
    // btDiscreteDynamicsWorld::removeRigidBody searches its body array and the hashed pair cache
    // walks every overlapping pair, so it is timed apart, with a harness integrator standing in for
    // the world. Per-removal ratio between a 10x larger and a smaller world, generous bound (the
    // old scan made it about 10x).
    [Fact]
    public void RemoveCube_ManagedCostDoesNotGrowWithTheWorld()
    {
        double small = MeasureRemovalMicroseconds(side: 24, harness: true);
        double large = MeasureRemovalMicroseconds(side: 76, harness: true);
        double smallWithBullet = MeasureRemovalMicroseconds(side: 24, harness: false);
        double largeWithBullet = MeasureRemovalMicroseconds(side: 76, harness: false);
        _output.WriteLine(
            $"per removal, managed: {small:0.0} us (~576 cubes), {large:0.0} us (~5776 cubes), ratio {large / small:0.00}; " +
            $"with Bullet: {smallWithBullet:0.0} us, {largeWithBullet:0.0} us");
        Assert.True(large < small * 4d + 20d, $"small={small:0.0}us large={large:0.0}us");
    }

    private static double MeasureRemovalMicroseconds(int side, bool harness)
    {
        using PhysicsIntegrator physics = harness
            ? new PhysicsIntegrator(_ => { }, _ => { }, DisposeBody)
            : new PhysicsIntegrator();
        Map map = new(physics, width: side + 2, height: 4, depth: side + 2);
        List<Cube> cubes = [];
        for (int x = 1; x <= side; x++)
        {
            for (int z = 1; z <= side; z++)
            {
                cubes.Add(map.TryAddCube(new Vector3(x, 1f, z), withPhysics: true)!);
            }
        }

        // Warm up the paths, then time a fixed number of removals spread over the world.
        Random random = new(3);
        List<Cube> order = cubes.OrderBy(_ => random.Next()).ToList();
        for (int i = 0; i < 20; i++)
        {
            map.RemoveCube(order[i]);
        }

        const int removals = 300;
        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int i = 20; i < 20 + removals; i++)
        {
            map.RemoveCube(order[i]);
        }

        stopwatch.Stop();
        return stopwatch.Elapsed.TotalMilliseconds * 1000d / removals;
    }

    private static void DisposeBody(RigidBody body)
    {
        body.MotionState?.Dispose();
        body.CollisionShape?.Dispose();
        body.Dispose();
    }
}
