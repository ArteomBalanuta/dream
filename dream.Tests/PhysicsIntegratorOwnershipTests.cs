using System.Reflection;
using System.Runtime.CompilerServices;
using BulletSharp;
using Xunit;
using TkVector3 = OpenTK.Mathematics.Vector3;

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
        Assert.Single(addedBodies, playerBody);
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

        Assert.Equal(playerBody, removedBodies[^1]);
        Assert.Single(disposedBodies, playerBody);
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
        Assert.Single(removedBodies, playerBody);
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
        return (RigidBody)RuntimeHelpers.GetUninitializedObject(typeof(RigidBody));
    }
}
