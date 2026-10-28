using BulletSharp;
using Dream.Simulation.Physics;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;
using TkVector3 = OpenTK.Mathematics.Vector3;

namespace Dream.Tests.Simulation.Physics;

// PhysicsSyncSystem measures a cube's movement from the center it last reported, not from the
// previous tick's center, so slow creep still reaches clients as CubeMoved deltas.
public class PhysicsSyncSystemLastReportedCenterTests
{
    [Fact]
    public void CreepBelowTheThresholdPerTick_IsReportedOnceTheAccumulatedDriftPassesIt()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, width: 10, depth: 10);
        Cube cube = AddDynamicCube(map, new TkVector3(4f, 1f, 4f));
        RigidBody body = BodyOf(physics, cube);
        PhysicsSyncSystem sync = new();
        TkVector3 start = cube.Position;
        List<int> reportedTicks = [];
        List<PhysicsCubeMoveDelta> reported = [];

        // 0.4 mm per tick: each tick alone is under the 1 mm threshold, three ticks are over it.
        for (int tick = 1; tick <= 10; tick++)
        {
            MoveBody(body, start + new TkVector3(0.0004f * tick, 0f, 0f));
            PhysicsSyncResult result = sync.SyncDynamicMapBodies(map, physics.DynamicMapCubeBodies);
            if (result.MovedCubes.Count > 0)
            {
                reportedTicks.Add(tick);
                reported.Add(Assert.Single(result.MovedCubes));
            }
        }

        Assert.Equal([3, 6, 9], reportedTicks);
        foreach (PhysicsCubeMoveDelta move in reported)
        {
            Assert.Same(cube, move.Cube);
            Assert.Equal(new TkVector3(4f, 1f, 4f), move.SourceCellPosition);
            Assert.Equal(new TkVector3(4f, 1f, 4f), move.TargetCellPosition);
        }

        Assert.Equal(start + new TkVector3(0.0004f * 9, 0f, 0f), reported[^1].CenterPosition);
    }

    [Fact]
    public void ACellChange_IsAlwaysReported_EvenBelowTheThreshold()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, width: 10, depth: 10);
        Cube cube = AddDynamicCube(map, new TkVector3(4f, 1f, 4f));
        RigidBody body = BodyOf(physics, cube);
        PhysicsSyncSystem sync = new();

        MoveBody(body, new TkVector3(4.9995f, cube.Position.Y, cube.Position.Z));
        PhysicsCubeMoveDelta approach = Assert.Single(sync.SyncDynamicMapBodies(map, physics.DynamicMapCubeBodies).MovedCubes);
        Assert.Equal(approach.SourceCellPosition, approach.TargetCellPosition);

        // 0.9 mm from the last reported center, but into the next cell.
        TkVector3 crossed = new(5.0004f, cube.Position.Y, cube.Position.Z);
        MoveBody(body, crossed);
        PhysicsCubeMoveDelta move = Assert.Single(sync.SyncDynamicMapBodies(map, physics.DynamicMapCubeBodies).MovedCubes);

        Assert.Same(cube, move.Cube);
        Assert.Equal(new TkVector3(4f, 1f, 4f), move.SourceCellPosition);
        Assert.Equal(new TkVector3(5f, 1f, 4f), move.TargetCellPosition);
        Assert.Equal(crossed, move.CenterPosition);
        Assert.True(map.TryGetCellForCube(cube, out GeometryCellKey cell));
        Assert.Equal(new GeometryCellKey(5, 1, 4), cell);
    }

    [Fact]
    public void ARestingOrSubThresholdJitteringCube_ProducesNoMoves()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, width: 10, depth: 10);
        Cube cube = AddDynamicCube(map, new TkVector3(4f, 1f, 4f));
        RigidBody body = BodyOf(physics, cube);
        PhysicsSyncSystem sync = new();
        TkVector3 start = cube.Position;
        int reported = 0;

        for (int tick = 0; tick < 100; tick++)
        {
            reported += sync.SyncDynamicMapBodies(map, physics.DynamicMapCubeBodies).MovedCubes.Count;
        }

        // Jitter of +-0.3 mm around the reported center never accumulates past the threshold.
        for (int tick = 0; tick < 100; tick++)
        {
            float offset = tick % 2 == 0 ? 0.0003f : -0.0003f;
            MoveBody(body, start + new TkVector3(offset, 0f, offset));
            reported += sync.SyncDynamicMapBodies(map, physics.DynamicMapCubeBodies).MovedCubes.Count;
        }

        Assert.Equal(0, reported);
        Assert.Equal(1, sync.TrackedCubeBaselineCount);
    }

    [Fact]
    public void RemovedAndFracturedCubes_DropTheirBaselines_SoTheStateStaysBounded()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, width: 16, depth: 16);
        AddDynamicCube(map, new TkVector3(1f, 1f, 1f));
        AddDynamicCube(map, new TkVector3(14f, 1f, 14f));
        PhysicsSyncSystem sync = new();
        sync.SyncDynamicMapBodies(map, physics.DynamicMapCubeBodies);
        Assert.Equal(2, sync.TrackedCubeBaselineCount);

        int churned = 0;
        for (int x = 3; x <= 13; x += 2)
        {
            for (int z = 3; z <= 13; z += 2)
            {
                Cube cube = AddDynamicCube(map, new TkVector3(x, 1f, z));
                sync.SyncDynamicMapBodies(map, physics.DynamicMapCubeBodies);
                Assert.Equal(3, sync.TrackedCubeBaselineCount);
                Assert.Equal(3, CountCubeBodies(physics));

                if (churned % 2 == 0)
                {
                    Assert.True(map.RemoveCube(cube));
                }
                else
                {
                    Assert.True(map.FractureCubeIntoPieces(new GeometryCellKey(x, 1, z)));
                }

                sync.SyncDynamicMapBodies(map, physics.DynamicMapCubeBodies);
                Assert.Equal(2, sync.TrackedCubeBaselineCount);
                churned++;
            }
        }

        Assert.Equal(36, churned);
    }

    private static Cube AddDynamicCube(Map map, TkVector3 cell)
    {
        return Assert.IsType<Cube>(map.TryAddCube(cell, isStatic: false, withPhysics: true));
    }

    private static RigidBody BodyOf(PhysicsIntegrator physics, Cube cube)
    {
        return Assert.Single(
            physics.DynamicMapCubeBodies,
            candidate => candidate.UserObject is ValueTuple<Cube, TkVector3> stored && ReferenceEquals(stored.Item1, cube));
    }

    private static int CountCubeBodies(PhysicsIntegrator physics)
    {
        return physics.DynamicMapCubeBodies.Count(body => body.UserObject is ValueTuple<Cube, TkVector3>);
    }

    private static void MoveBody(RigidBody body, TkVector3 center)
    {
        BulletSharp.Math.Matrix transform = BulletSharp.Math.Matrix.Translation(center.ToBullet());
        body.WorldTransform = transform;
        body.MotionState.SetWorldTransform(ref transform);
        body.LinearVelocity = BulletSharp.Math.Vector3.Zero;
    }
}
