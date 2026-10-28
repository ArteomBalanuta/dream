using BulletSharp;
using Dream.Simulation.Physics;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Xunit;
using Xunit.Abstractions;
using TkVector3 = OpenTK.Mathematics.Vector3;

namespace Dream.Tests.Simulation.Physics;

// Cell supports (WorldPhysicsBinder.SyncCellSupportBody): every occupied partial static cell gets a
// full-cell static box that only dynamic map cubes collide with, so physics agrees with the map's
// occupancy rule (Map.CanTransitionCubeToCell treats the cell as full) and cubes resting on partial
// terrain settle instead of cycling through reject/reset (see Wave8PartialTerrainRestingCubeTests).
public class PartialCellSupportTests
{
    private const float TickSeconds = 1f / 64f;

    private readonly ITestOutputHelper _output;

    public PartialCellSupportTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void StartupTerrain_SupportsExactlyTheOccupiedPartialStaticCells()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, generateStartupTerrain: true, width: 56, depth: 56);

        int partialCells = 0;
        foreach ((GeometryCellKey key, GeometryCellRecord record) in map.GeometryCells)
        {
            bool partial = key.Y > 0 && record.SubdivisionState != null && map.Cubes[key.X, key.Y, key.Z] != null;
            if (partial)
            {
                partialCells++;
            }

            Assert.Equal(partial, map.HasCellSupportBody(key));
        }

        _output.WriteLine($"partial static cells / supports: {partialCells} / {map.CellSupportBodyCount}");
        Assert.True(partialCells > 0);
        Assert.Equal(partialCells, map.CellSupportBodyCount);

        // A full placed cube, static or dynamic, needs none.
        Cube placed = Assert.IsType<Cube>(map.TryAddCube(new TkVector3(3f, 1f, 3f), withPhysics: true));
        Assert.True(map.TryGetCellForCube(placed, out GeometryCellKey placedCell));
        Assert.False(map.HasCellSupportBody(placedCell));
        Assert.Equal(partialCells, map.CellSupportBodyCount);
    }

    [Fact]
    public void Support_CollidesOnlyWithDynamicMapCubes()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, generateStartupTerrain: true, width: 56, depth: 56);
        RigidBody support = physics.MapCubeBodies.First(body => body.UserObject is GeometryCellSupportReference);

        int group = (int)support.BroadphaseProxy.CollisionFilterGroup;
        int mask = (int)support.BroadphaseProxy.CollisionFilterMask;
        Assert.Equal((int)CollisionFilterGroups.StaticFilter, group);
        Assert.Equal((int)CollisionFilterGroups.DefaultFilter, mask);
        // Players and fracture rubble never touch it; dynamic cubes (default group) do.
        Assert.Equal(0, mask & (int)PhysicsIntegrator.PlayerCollisionGroup);
        Assert.Equal(0, mask & (int)PhysicsIntegrator.DebrisCollisionGroup);
        Assert.Equal(0, mask & (int)CollisionFilterGroups.StaticFilter);
        Assert.True(support.IsStaticObject);
        Assert.DoesNotContain(support, physics.DynamicMapCubeBodies);
    }

    [Fact]
    public void RemovingAPartialCell_RemovesItsSupport()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, generateStartupTerrain: true, width: 56, depth: 56);
        GeometryCellKey partial = new(26, 2, 13);
        Cube cube = Assert.IsType<Cube>(map.Cubes[partial.X, partial.Y, partial.Z]);
        Assert.True(map.HasCellSupportBody(partial));
        Assert.True(physics.MapCubeBodies.TryGetSupport(partial, out RigidBody? support));

        Assert.True(map.RemoveCube(cube));

        Assert.False(map.HasCellSupportBody(partial));
        Assert.DoesNotContain(support!, physics.MapCubeBodies);
        Assert.Empty(map.FindInvariantViolations());

        // Restoring the startup cell brings it back.
        Assert.True(map.RestoreGeneratedStartupCell(partial));
        Assert.True(map.HasCellSupportBody(partial));
    }

    // A one-layer carpet of touching cubes over the sculptures (pyramid, boulder, rubble cluster),
    // laid like the load seeder lays cubes: one per column, right above the column's top occupied
    // cell. Many of them rest on partial cells, and the carpet is one contact island, so before the
    // fix a single cycling cube kept every body in it awake and synced every tick.
    [Fact]
    public void CarpetOverPartialCells_SettlesWithoutResetsAndTheIslandSleeps()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, generateStartupTerrain: true, width: 56, depth: 56);
        List<(Cube Cube, RigidBody Body)> carpet = [];
        int onPartialCells = 0;
        for (int x = 14; x <= 38; x++)
        {
            for (int z = 8; z <= 24; z++)
            {
                int top = TopOccupiedY(map, x, z);
                if (top < 0 || top + 1 >= map.Height - 1)
                {
                    continue;
                }

                Cube cube = Assert.IsType<Cube>(map.TryAddCube(new TkVector3(x, top + 1, z), withPhysics: true));
                RigidBody body = physics.DynamicMapCubeBodies.Single(candidate =>
                    candidate.UserObject is ValueTuple<Cube, TkVector3> tuple && ReferenceEquals(tuple.Item1, cube));
                carpet.Add((cube, body));
                if (top > 0 && map.TryGetGeometryCellAtCell(x, top, z)?.SubdivisionState != null)
                {
                    onPartialCells++;
                }
            }
        }

        Assert.True(onPartialCells >= 20, $"only {onPartialCells} carpet cubes rest on partial cells");

        PhysicsSyncSystem sync = new();
        float[] previousY = carpet.Select(entry => entry.Body.WorldTransform.Origin.Y).ToArray();
        const int ticks = 640;
        int resetsInLastHalf = 0;
        int activeAtEnd = 0;
        int syncedAtEnd = 0;
        for (int tick = 0; tick < ticks; tick++)
        {
            physics.StepSimulationFixed(TickSeconds);
            sync.SyncDynamicMapBodies(map, physics.DynamicMapCubeBodies);
            for (int i = 0; i < carpet.Count; i++)
            {
                float y = carpet[i].Body.WorldTransform.Origin.Y;
                if (tick >= ticks / 2 && y - previousY[i] > 0.01f)
                {
                    resetsInLastHalf++;
                }

                previousY[i] = y;
            }

            if (tick % 64 == 63)
            {
                int active = carpet.Count(entry => entry.Body.IsActive);
                _output.WriteLine($"tick {tick + 1}: active {active}/{carpet.Count}, synced {sync.LastSyncedBodyCount}");
            }

            if (tick == ticks - 1)
            {
                activeAtEnd = carpet.Count(entry => entry.Body.IsActive);
                syncedAtEnd = sync.LastSyncedBodyCount;
            }
        }

        _output.WriteLine($"carpet {carpet.Count} cubes, {onPartialCells} on partial cells, {sync.TotalRejectionResets} rejection resets");
        Assert.Equal(0, resetsInLastHalf);
        Assert.Equal(0, sync.TotalRejectionResets);
        Assert.Equal(0, activeAtEnd);
        Assert.Equal(0, syncedAtEnd);
        foreach ((Cube cube, RigidBody body) in carpet)
        {
            Assert.True(map.TryGetCellForCube(cube, out GeometryCellKey cell));
            Assert.InRange(body.WorldTransform.Origin.Y - cell.Y, 0.45f, 0.55f);
        }

        Assert.Empty(map.FindInvariantViolations());
    }

    // The reset counter: a cube above a cell the map holds but physics does not (a cube added
    // without physics) falls into it, is rejected and reset every RejectionsBeforeReset syncs.
    [Fact]
    public void TotalRejectionResets_CountsEveryResetToTheAcceptedCenter()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, width: 12, depth: 12);
        Assert.NotNull(map.TryAddCube(new TkVector3(5f, 1f, 5f), withPhysics: false));
        Cube cube = Assert.IsType<Cube>(map.TryAddCube(new TkVector3(5f, 2f, 5f), withPhysics: true));
        RigidBody body = physics.DynamicMapCubeBodies.Single(candidate =>
            candidate.UserObject is ValueTuple<Cube, TkVector3> tuple && ReferenceEquals(tuple.Item1, cube));

        PhysicsSyncSystem sync = new();
        int observedResets = 0;
        float previousY = body.WorldTransform.Origin.Y;
        for (int tick = 0; tick < 256; tick++)
        {
            physics.StepSimulationFixed(TickSeconds);
            sync.SyncDynamicMapBodies(map, physics.DynamicMapCubeBodies);
            float y = body.WorldTransform.Origin.Y;
            if (y - previousY > 0.01f)
            {
                observedResets++;
            }

            previousY = y;
        }

        _output.WriteLine($"observed {observedResets}, counted {sync.TotalRejectionResets}");
        Assert.True(observedResets >= 10, $"observed {observedResets}");
        Assert.Equal(observedResets, sync.TotalRejectionResets);
    }

    private static int TopOccupiedY(Map map, int x, int z)
    {
        for (int y = map.Height - 1; y >= 0; y--)
        {
            if (map.Cubes[x, y, z] != null)
            {
                return y;
            }
        }

        return -1;
    }
}

internal static class SupportBodyLookup
{
    public static bool TryGetSupport(this InsertionOrderedSet<RigidBody> bodies, GeometryCellKey cell, out RigidBody? support)
    {
        foreach (RigidBody body in bodies)
        {
            if (body.UserObject is GeometryCellSupportReference reference && reference.CellKey == cell)
            {
                support = body;
                return true;
            }
        }

        support = null;
        return false;
    }
}
