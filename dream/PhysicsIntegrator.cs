using BulletSharp;
using BulletSharp.Math;
using System.Collections.Generic;

public enum PhysicsBodyOwnership
{
    CallerOwned,
    IntegratorOwned
}

public class PhysicsIntegrator : IDisposable
{
    public DiscreteDynamicsWorld World { get; }
    public RigidBody? PlayerBody { get; private set; }
    public RigidBody? FallingCubeBody { get; }
    public List<RigidBody> MapCubeBodies { get; } = new();
    public List<RigidBody> DynamicMapCubeBodies { get; } = new();

    private readonly DefaultCollisionConfiguration _collisionConfig;
    private readonly CollisionDispatcher _dispatcher;
    private readonly BroadphaseInterface _broadphase;
    private readonly SequentialImpulseConstraintSolver _solver;
    private PhysicsBodyOwnership? _playerBodyOwnership;
    private readonly Action<RigidBody> _addRigidBodyToWorld;
    private readonly Action<RigidBody> _removeRigidBodyFromWorld;
    private readonly Action<RigidBody> _disposeRigidBody;

    public PhysicsIntegrator()
    {
        _collisionConfig = new DefaultCollisionConfiguration();
        _dispatcher = new CollisionDispatcher(_collisionConfig);
        _broadphase = new DbvtBroadphase();
        _solver = new SequentialImpulseConstraintSolver();
        World = new DiscreteDynamicsWorld(_dispatcher, _broadphase, _solver, _collisionConfig);
        World.Gravity = new BulletSharp.Math.Vector3(0, -9.8f, 0);
        World.SolverInfo.NumIterations = 10;
        World.SolverInfo.SolverMode = SolverModes.UseWarmStarting;
        World.DispatchInfo.UseContinuous = true;
        _addRigidBodyToWorld = World.AddRigidBody;
        _removeRigidBodyFromWorld = World.RemoveRigidBody;
        _disposeRigidBody = DisposeBody;
    }

    internal PhysicsIntegrator(
        Action<RigidBody> addRigidBodyToWorld,
        Action<RigidBody> removeRigidBodyFromWorld,
        Action<RigidBody> disposeRigidBody)
    {
        ArgumentNullException.ThrowIfNull(addRigidBodyToWorld);
        ArgumentNullException.ThrowIfNull(removeRigidBodyFromWorld);
        ArgumentNullException.ThrowIfNull(disposeRigidBody);

        World = null!;
        _collisionConfig = null!;
        _dispatcher = null!;
        _broadphase = null!;
        _solver = null!;
        _addRigidBodyToWorld = addRigidBodyToWorld;
        _removeRigidBodyFromWorld = removeRigidBodyFromWorld;
        _disposeRigidBody = disposeRigidBody;
    }

    public void StepSimulation(float deltaTime)
    {
        World.StepSimulation(deltaTime, 10);
    }

    public void StepSimulationFixed(float fixedDeltaTime)
    {
        World.StepSimulation(fixedDeltaTime, 0);
    }

    public void RegisterPlayerBody(RigidBody playerBody, PhysicsBodyOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(playerBody);

        if (PlayerBody != null)
        {
            throw new InvalidOperationException("A player body is already registered with this physics integrator.");
        }

        PlayerBody = playerBody;
        _playerBodyOwnership = ownership;
        _addRigidBodyToWorld(playerBody);
    }

    public RigidBody DetachPlayerBody()
    {
        if (PlayerBody == null)
        {
            throw new InvalidOperationException("No player body is currently registered with this physics integrator.");
        }

        RigidBody playerBody = PlayerBody;
        _removeRigidBodyFromWorld(playerBody);
        PlayerBody = null;
        _playerBodyOwnership = null;
        return playerBody;
    }

    public void ResetMapBodies()
    {
        foreach (RigidBody body in MapCubeBodies)
        {
            if (body == null)
            {
                continue;
            }

            _removeRigidBodyFromWorld(body);
            _disposeRigidBody(body);
        }

        MapCubeBodies.Clear();
        DynamicMapCubeBodies.Clear();
    }

    public void Dispose()
    {
        if (PlayerBody != null)
        {
            _removeRigidBodyFromWorld(PlayerBody);
            if (_playerBodyOwnership == PhysicsBodyOwnership.IntegratorOwned)
            {
                _disposeRigidBody(PlayerBody);
            }

            PlayerBody = null;
            _playerBodyOwnership = null;
        }

        FallingCubeBody?.Dispose();
        ResetMapBodies();
        World?.Dispose();
        _solver?.Dispose();
        _broadphase?.Dispose();
        _dispatcher?.Dispose();
        _collisionConfig?.Dispose();
    }

    private static void DisposeBody(RigidBody body)
    {
        body.MotionState?.Dispose();
        body.CollisionShape?.Dispose();
        body.Dispose();
    }
}
