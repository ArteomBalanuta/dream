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
        Assert.NotEqual(BulletSharp.ActivationState.DisableDeactivation, body.ActivationState);
        Assert.True(body.ContactProcessingThreshold > 0f);
        Assert.True(body.CcdMotionThreshold > 0f);
        Assert.True(body.CcdSweptSphereRadius > 0f);
        Assert.True(body.CcdSweptSphereRadius <= body.CcdMotionThreshold);
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

    [Fact]
    public void UpdateCubePosition_WithDeferredPhysicsSync_KeepsMovedCubeBodyWhenOldCellIsReused()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, loadVisualAssets: false, width: 8, depth: 8);
        Cube movedCube = Assert.IsType<Cube>(map.TryAddCube(new Vector3(3f, 1f, 3f), withPhysics: true));

        map.UpdateCubePosition(movedCube, new Vector3(4.2f, 1.5f, 3.5f), syncPhysics: false);
        Cube replacementCube = Assert.IsType<Cube>(map.TryAddCube(new Vector3(3f, 1f, 3f), withPhysics: true));

        Assert.Equal(2, physics.DynamicMapCubeBodies.Count);
        Assert.Contains(
            physics.DynamicMapCubeBodies,
            body => body.UserObject is ValueTuple<Cube, Vector3> cubeBody && ReferenceEquals(cubeBody.Item1, movedCube));
        Assert.Contains(
            physics.DynamicMapCubeBodies,
            body => body.UserObject is ValueTuple<Cube, Vector3> cubeBody && ReferenceEquals(cubeBody.Item1, replacementCube));
    }
}
