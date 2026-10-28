using BulletSharp;
using Dream.Simulation.Physics;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Xunit;
using Xunit.Abstractions;
using TkVector3 = OpenTK.Mathematics.Vector3;

namespace Dream.Tests.Simulation.Physics;

// Wave 8 tester, SCALE-5 x partial terrain.
//
// The enriched startup terrain has sculpture cells (WorldGenerator.CreateStartupSculptureCells):
// cells whose GeometrySubdivisionState holds only some solid microcells. Each one is materialized
// as a compatibility Cube in Map.Cubes (Map.MaterializeGeometryCell), so for cell transitions it is
// a full, occupied cell (Map.CanTransitionCubeToCell, Map.cs:1607-1618), while its collision is only
// the solid microcells. (26, 2, 13) belongs to the low rubble cluster ellipsoid centred at
// (27.2, 1.45, 14.4) with a vertical radius of 0.7: only its bottom ~0.15 m is solid.
//
// A cube placed on top of it, at (26, 3, 13), has nothing under it but that sliver: physics lets it
// fall into (26, 2, 13), the map rejects the transition every sync, and after
// PhysicsSyncSystem.RejectionsBeforeReset (8) rejections ResetBodyToCenter (PhysicsSyncSystem.cs:571)
// teleports it back to the last accepted center (y = 3.0 + epsilon, the lowest point still inside
// (26, y=3, 13)), zeroes its velocity and calls Activate(true). It falls again. Measured: a reset
// every 8 ticks (free fall of ~0.08 m between resets, it never reaches the sliver), forever; the
// body never sleeps, and with SCALE-5 it (and everything in its contact island) is synced every
// tick. No mutation is exported (the map center stays at y = 3.0008), so it is invisible on the
// wire and only costs server CPU and keeps islands awake.
public class Wave8PartialTerrainRestingCubeTests
{
    private const float TickSeconds = 1f / 64f;
    private const int Ticks = 640;

    private readonly ITestOutputHelper _output;

    public Wave8PartialTerrainRestingCubeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // Current behaviour, asserted so a fix shows up as a failure here and the skipped target below
    // can be enabled.
    [Fact]
    public void CubeOnPartialSculptureCell_CurrentlyCyclesRejectResetAndNeverSleeps()
    {
        CycleReport report = Run();
        _output.WriteLine(report.ToString());

        Assert.True(report.PartialCellOccupied, "(26, 2, 13) should be a materialized sculpture cell");
        Assert.True(report.PartialCellHasSubdivision, "(26, 2, 13) should carry a subdivision state");
        Assert.True(report.Resets >= 10, report.ToString());
        Assert.True(report.ResetsInLastHalf >= 5, report.ToString());
        Assert.Equal(0, report.SleepingTicksInLastHalf);
    }

    [Fact(Skip = "bug: SCALE-5 x partial terrain. A cube resting on a partial sculpture cell (26,2,13) falls into the cell, " +
                 "Map.CanTransitionCubeToCell rejects it (the cell is a materialized compatibility cube), and PhysicsSyncSystem " +
                 "resets it to the unsupported center above with Activate(true) every RejectionsBeforeReset ticks, forever. " +
                 "Target: the cube settles (no resets) and its body sleeps within 5 s.")]
    public void CubeOnPartialSculptureCell_TargetSettlesAndSleeps()
    {
        CycleReport report = Run();
        _output.WriteLine(report.ToString());

        Assert.Equal(0, report.ResetsInLastHalf);
        Assert.True(report.SleepingTicksInLastHalf > Ticks / 4, report.ToString());
    }

    private static CycleReport Run()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, generateStartupTerrain: true, width: 56, depth: 56);
        Cube? partial = map.Cubes[26, 2, 13];
        GeometryCellRecord? record = map.TryGetGeometryCellAtCell(26, 2, 13);
        bool occupied = partial != null;
        bool hasSubdivision = record?.SubdivisionState != null;
        Assert.Null(map.Cubes[26, 3, 13]);

        Cube cube = Assert.IsType<Cube>(map.TryAddCube(new TkVector3(26f, 3f, 13f), withPhysics: true));
        RigidBody body = physics.DynamicMapCubeBodies.Single(candidate =>
            candidate.UserObject is ValueTuple<Cube, TkVector3> tuple && ReferenceEquals(tuple.Item1, cube));

        PhysicsSyncSystem sync = new();
        float previousY = body.WorldTransform.Origin.Y;
        float minY = previousY;
        int resets = 0;
        int resetsInLastHalf = 0;
        int sleepingInLastHalf = 0;
        int rejectedTicks = 0;
        for (int tick = 0; tick < Ticks; tick++)
        {
            physics.StepSimulationFixed(TickSeconds);
            sync.SyncDynamicMapBodies(map, physics.DynamicMapCubeBodies);
            float y = body.WorldTransform.Origin.Y;
            minY = Math.Min(minY, y);
            if (y - previousY > 0.01f)
            {
                resets++;
                if (tick >= Ticks / 2)
                {
                    resetsInLastHalf++;
                }
            }

            if (map.TryGetCellForCube(cube, out GeometryCellKey cell) && cell.Y == 3 && y < 3f)
            {
                rejectedTicks++;
            }

            if (tick >= Ticks / 2 && !body.IsActive)
            {
                sleepingInLastHalf++;
            }

            previousY = y;
        }

        map.TryGetCellForCube(cube, out GeometryCellKey finalCell);
        return new CycleReport(occupied, hasSubdivision, resets, resetsInLastHalf, sleepingInLastHalf, rejectedTicks, minY, finalCell);
    }

    private sealed record CycleReport(
        bool PartialCellOccupied,
        bool PartialCellHasSubdivision,
        int Resets,
        int ResetsInLastHalf,
        int SleepingTicksInLastHalf,
        int TicksBelowAcceptedCell,
        float MinBodyY,
        GeometryCellKey FinalMapCell);
}
