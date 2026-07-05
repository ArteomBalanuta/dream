using System.Reflection;
using System.Runtime.CompilerServices;
using BulletSharp;
using Xunit;
using TkVector3 = OpenTK.Mathematics.Vector3;
using Dream.Simulation.Physics;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Simulation.Physics;


public class PhysicsIntegratorOwnershipTests
{
    [Fact]
    public void RegisterPlayerBody_AssignsIntegratorSlotAndRecordsWorldRegistration()
    {
        List<RigidBody> addedBodies = new();
        PhysicsIntegrator physics = CreateHarness(
            addRigidBodyToWorld: addedBodies.Add,
            removeRigidBodyFromWorld: _ => { },
            disposeRigidBody: _ => { });
        RigidBody playerBody = CreateBodyToken();

        physics.RegisterPlayerBody(playerBody, PhysicsBodyOwnership.IntegratorOwned);

        Assert.Same(playerBody, physics.PlayerBody);
        Assert.Single(addedBodies);
        Assert.Same(playerBody, addedBodies[0]);
    }

    [Fact]
    public void Dispose_WhenIntegratorOwnsPlayerBody_DisposesItAfterWorldRemoval()
    {
        List<RigidBody> removedBodies = new();
        List<RigidBody> disposedBodies = new();
        PhysicsIntegrator physics = CreateHarness(
            addRigidBodyToWorld: _ => { },
            removeRigidBodyFromWorld: removedBodies.Add,
            disposeRigidBody: disposedBodies.Add);
        RigidBody playerBody = CreateBodyToken();

        physics.RegisterPlayerBody(playerBody, PhysicsBodyOwnership.CallerOwned);
        physics.DetachPlayerBody();
        physics.RegisterPlayerBody(playerBody, PhysicsBodyOwnership.IntegratorOwned);

        physics.Dispose();

        Assert.Same(playerBody, removedBodies[^1]);
        Assert.Single(disposedBodies);
        Assert.Same(playerBody, disposedBodies[0]);
    }

    [Fact]
    public void DetachPlayerBody_RemovesBodyFromWorldWithoutDisposingAndClearsIntegratorSlot()
    {
        List<RigidBody> removedBodies = new();
        List<RigidBody> disposedBodies = new();
        PhysicsIntegrator physics = CreateHarness(
            addRigidBodyToWorld: _ => { },
            removeRigidBodyFromWorld: removedBodies.Add,
            disposeRigidBody: disposedBodies.Add);
        RigidBody playerBody = CreateBodyToken();
        physics.RegisterPlayerBody(playerBody, PhysicsBodyOwnership.CallerOwned);

        RigidBody detachedBody = physics.DetachPlayerBody();

        Assert.Same(playerBody, detachedBody);
        Assert.Null(physics.PlayerBody);
        Assert.Single(removedBodies);
        Assert.Same(playerBody, removedBodies[0]);
        Assert.Empty(disposedBodies);
    }

    [Fact]
    public void PlayerConstructor_ThrowsWhenBodyIsNotTheIntegratorRegisteredPlayerBody()
    {
        PhysicsIntegrator physics = CreateHarness(
            addRigidBodyToWorld: _ => { },
            removeRigidBodyFromWorld: _ => { },
            disposeRigidBody: _ => { });
        Map map = new(loadVisualAssets: false);
        RigidBody registeredBody = CreateBodyToken();
        RigidBody otherBody = CreateBodyToken();
        physics.RegisterPlayerBody(registeredBody, PhysicsBodyOwnership.IntegratorOwned);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            new Player(map, physics, otherBody, new TkVector3(5f, 1.5f, 5f)));

        Assert.Contains("registered player body", ex.Message);
    }

    [Fact]
    public void Map_With_Physics_Registers_Floor_And_Placed_Cube_Bodies_From_GeometryCollisionExport()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, loadVisualAssets: false, width: 8, height: 8, depth: 8);

        Assert.Single(physics.MapCubeBodies);

        map.AddCube(new TkVector3(3f, 1f, 3f), withPhysics: true);

        Assert.Equal(2, physics.MapCubeBodies.Count);
        Assert.Single(physics.DynamicMapCubeBodies);
        GeometryCollisionExport export = map.CaptureGeometryCollisionExport();
        Assert.Contains(export.Bodies, body => body.Kind == GeometryCollisionBodyKind.Cell && body.CellKey == new GeometryCellKey(3, 1, 3));
    }

    [Fact]
    public void Fractured_Cell_Physics_Bodies_Remain_Registered_For_Debug_Rendering()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, loadVisualAssets: false, width: 8, height: 8, depth: 8);
        map.AddCube(new TkVector3(3f, 1f, 3f), withPhysics: true);

        bool fractured = map.FractureCubeIntoPieces(new GeometryCellKey(3, 1, 3));

        Assert.True(fractured);
        Assert.Contains(
            physics.MapCubeBodies,
            body => body.UserObject is GeometryDynamicBodyReference reference &&
                reference.CellKey == new GeometryCellKey(3, 1, 3));
    }

    [Fact]
    public void Fractured_Cell_Physics_Bodies_AreDynamicSoRubbleCanBePushed()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, loadVisualAssets: false, width: 8, height: 8, depth: 8);
        map.AddCube(new TkVector3(3f, 1f, 3f), withPhysics: true);

        map.FractureCubeIntoPieces(new GeometryCellKey(3, 1, 3));

        Assert.Contains(
            physics.DynamicMapCubeBodies,
            body => body.UserObject is GeometryDynamicBodyReference reference &&
                reference.CellKey == new GeometryCellKey(3, 1, 3));
    }

    [Fact]
    public void FractureCubeIntoPieces_RemovesCompatibilityBodyAfterPhysicsDrivenCellDrift()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, loadVisualAssets: false, width: 8, height: 8, depth: 8);
        map.AddCube(new TkVector3(3f, 1f, 3f), withPhysics: true);
        Cube cube = Assert.IsType<Cube>(map.TryGetCubeAtCell(3, 1, 3));

        map.UpdateCubePosition(cube, new TkVector3(4.2f, 1.5f, 3.5f), syncPhysics: false);
        bool fractured = map.FractureCubeIntoPieces(new GeometryCellKey(4, 1, 3));

        Assert.True(fractured);
        Assert.DoesNotContain(
            physics.MapCubeBodies,
            body => body.UserObject is ValueTuple<Cube, TkVector3> cubeBody &&
                ReferenceEquals(cubeBody.Item1, cube));
        Assert.Contains(
            physics.DynamicMapCubeBodies,
            body => body.UserObject is GeometryDynamicBodyReference reference &&
                reference.CellKey == new GeometryCellKey(4, 1, 3));
    }

    private static PhysicsIntegrator CreateHarness(
        Action<RigidBody> addRigidBodyToWorld,
        Action<RigidBody> removeRigidBodyFromWorld,
        Action<RigidBody> disposeRigidBody)
    {
        ConstructorInfo ctor = typeof(PhysicsIntegrator).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            new[]
            {
                typeof(Action<RigidBody>),
                typeof(Action<RigidBody>),
                typeof(Action<RigidBody>)
            },
            modifiers: null)!;

        return (PhysicsIntegrator)ctor.Invoke(new object[]
        {
            addRigidBodyToWorld,
            removeRigidBodyFromWorld,
            disposeRigidBody
        });
    }

    private static RigidBody CreateBodyToken()
    {
        RigidBody body = (RigidBody)RuntimeHelpers.GetUninitializedObject(typeof(RigidBody));
        GC.SuppressFinalize(body);
        return body;
    }
}
