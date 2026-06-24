using BulletSharp;
using BulletSharp.Math;
using System.Collections.Generic;

public class PhysicsIntegrator : IDisposable
{
    public DiscreteDynamicsWorld World { get; }
    public RigidBody PlayerBody { get; }
    public RigidBody FallingCubeBody { get; }
    public List<RigidBody> MapCubeBodies { get; } = new();
    public List<RigidBody> DynamicMapCubeBodies { get; } = new();

    private readonly DefaultCollisionConfiguration _collisionConfig;
    private readonly CollisionDispatcher _dispatcher;
    private readonly BroadphaseInterface _broadphase;
    private readonly SequentialImpulseConstraintSolver _solver;
    private readonly PhysicsFactory _factory = new();

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

        PlayerBody = _factory.CreatePlayerBody(new Vector3(5.5f, 1.5f, 5.5f));
        World.AddRigidBody(PlayerBody);
    }

    public void StepSimulation(float deltaTime)
    {
        World.StepSimulation(deltaTime, 10);
    }

    public void ResetMapBodies()
    {
        foreach (RigidBody body in MapCubeBodies)
        {
            if (body == null)
            {
                continue;
            }

            World.RemoveRigidBody(body);
            body.MotionState?.Dispose();
            body.CollisionShape?.Dispose();
            body.Dispose();
        }

        MapCubeBodies.Clear();
        DynamicMapCubeBodies.Clear();
    }

    public void Dispose()
    {
        PlayerBody?.Dispose();
        FallingCubeBody?.Dispose();
        ResetMapBodies();
        World?.Dispose();
        _solver?.Dispose();
        _broadphase?.Dispose();
        _dispatcher?.Dispose();
        _collisionConfig?.Dispose();
    }
}
