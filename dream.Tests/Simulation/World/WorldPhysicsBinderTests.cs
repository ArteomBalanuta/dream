using OpenTK.Mathematics;
using Xunit;
using Dream.Simulation.Physics;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Simulation.World;


public class WorldPhysicsBinderTests
{
    [Fact]
    public void SyncCellCollisionBodies_ConfiguresDynamicFracturePiecesForImmediateContacts()
    {
        using PhysicsIntegrator physics = new();
        WorldPhysicsBinder binder = new(physics);
        GeometryCellKey cell = new(1, 1, 1);

        binder.SyncCellCollisionBodies(
            cell,
            [
                new GeometryCollisionBodyDescriptor(
                    GeometryCollisionBodyKind.Cell,
                    Center: new Vector3(1f, 1f, 1f),
                    HalfExtents: new Vector3(0.15f),
                    IsStatic: false,
                    CellKey: cell,
                    FracturePieceIndex: 0)
            ]);

        BulletSharp.RigidBody body = Assert.Single(physics.DynamicMapCubeBodies);
        Assert.Equal(BulletSharp.ActivationState.DisableDeactivation, body.ActivationState);
        Assert.Equal(0f, body.ContactProcessingThreshold);
        Assert.True(body.CcdMotionThreshold > 0f);
        Assert.True(body.CcdSweptSphereRadius > 0f);
    }

    [Fact]
    public void UpdateCubePosition_WithPhysicsSync_RebuildsSameCellCompatibilityBodyAtExactCenter()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, loadVisualAssets: false, width: 8, depth: 8);
        Cube cube = Assert.IsType<Cube>(map.TryAddCube(new Vector3(3f, 1f, 3f), withPhysics: true));
        Assert.Single(physics.DynamicMapCubeBodies);

        Vector3 authoritativeCenter = new(3.72f, 1.5f, 3.18f);
        map.UpdateCubePosition(cube, authoritativeCenter, syncPhysics: true);

        BulletSharp.RigidBody body = Assert.Single(physics.DynamicMapCubeBodies);
        body.MotionState.GetWorldTransform(out BulletSharp.Math.Matrix transform);
        Assert.Same(cube, map.TryGetCubeAtCell(3, 1, 3));
        Assert.Equal(authoritativeCenter.X, transform.Origin.X, 3);
        Assert.Equal(authoritativeCenter.Y, transform.Origin.Y, 3);
        Assert.Equal(authoritativeCenter.Z, transform.Origin.Z, 3);
    }
}
