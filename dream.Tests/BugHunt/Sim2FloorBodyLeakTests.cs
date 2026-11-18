using Dream.Simulation.Physics;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-SIM2-6: AddFloorBody recorded its body in no index — only in _floorBody, which was
// overwritten and never read — so a second floor descriptor stacked another static body in the
// Bullet world that nothing could ever remove. SyncMergedStaticCollisionBodies runs on every
// carve, so one leaked body per carve.
public class Sim2FloorBodyLeakTests
{
    [Fact]
    public void ASecondFloorDescriptor_ReplacesTheFloorBody_InsteadOfLeakingOne()
    {
        using PhysicsIntegrator physics = new();
        WorldPhysicsBinder binder = new(physics);
        GeometryCollisionBodyDescriptor floor = Floor();

        binder.SyncMergedStaticCollisionBodies([floor]);
        int afterFirst = physics.MapCubeBodies.Count;
        Assert.Equal(1, afterFirst);

        binder.SyncMergedStaticCollisionBodies([floor]);
        binder.SyncMergedStaticCollisionBodies([floor]);

        // Before the fix each call added another floor body nothing owned: 3 instead of 1.
        Assert.Equal(afterFirst, physics.MapCubeBodies.Count);
    }

    [Fact]
    public void AFloorDescriptorReachingACellSync_DoesNotStackFloorBodiesEither()
    {
        using PhysicsIntegrator physics = new();
        WorldPhysicsBinder binder = new(physics);
        GeometryCellKey cell = new(2, 1, 2);

        binder.SyncCellCollisionBodies(cell, [Floor()]);
        binder.SyncCellCollisionBodies(cell, [Floor()]);

        Assert.Equal(1, physics.MapCubeBodies.Count);
    }

    private static GeometryCollisionBodyDescriptor Floor() =>
        new(
            GeometryCollisionBodyKind.Floor,
            new Vector3(8f, 0f, 8f),
            new Vector3(8f, 0.5f, 8f),
            IsStatic: true);
}
