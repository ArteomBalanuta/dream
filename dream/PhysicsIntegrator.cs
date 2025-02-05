// PhysicsIntegrator.cs
using BulletSharp;
using BulletSharp.Math;
using BulletSharp.SoftBody;
using System.Collections.Generic;

public class PhysicsIntegrator : IDisposable
{
    public DiscreteDynamicsWorld World { get; }
    public RigidBody PlayerBody { get; }
    public RigidBody FallingCubeBody { get; }
    public List<RigidBody> MapCubeBodies { get; } = new List<RigidBody>();

    private readonly DefaultCollisionConfiguration _collisionConfig;
    private readonly CollisionDispatcher _dispatcher;
    private readonly BroadphaseInterface _broadphase;
    private readonly SequentialImpulseConstraintSolver _solver;

    public PhysicsIntegrator()
    {
        // Physics world setup
        _collisionConfig = new DefaultCollisionConfiguration();
        _dispatcher = new CollisionDispatcher(_collisionConfig);
        _broadphase = new DbvtBroadphase();
        _solver = new SequentialImpulseConstraintSolver();
        World = new DiscreteDynamicsWorld(_dispatcher, _broadphase, _solver, _collisionConfig);
        World.Gravity = new BulletSharp.Math.Vector3(0, -10, 0);
        World.SolverInfo.NumIterations = 10;
        World.SolverInfo.SolverMode = SolverModes.UseWarmStarting;
        World.DispatchInfo.UseContinuous = true;

        // Create player body with corrected Y position
        PlayerBody = CreatePlayerBody(new Vector3(5.5f, 1.5f, 5.5f)); // Adjusted Y to 1.5
        World.AddRigidBody(PlayerBody);

        // Falling cube
        FallingCubeBody = CreateFallingCube();
        World.AddRigidBody(PlayerBody);

    }

    private RigidBody CreatePlayerBody(Vector3 position)
    {
        // Use box shape for sharper collisions
        var shape = new BoxShape(0.5f, 1f, 0.5f);
        float mass = 1.0f;


        var startTransform = Matrix.Translation(position);
        var motionState = new DefaultMotionState(startTransform);

        shape.CalculateLocalInertia(mass, out var inertia);
        var rbInfo = new RigidBodyConstructionInfo(mass, motionState, shape, inertia);

        var body = new RigidBody(rbInfo)
        {
            Friction = 0.5f,
            Restitution = 0.0f,
            // Prevent rotation so that the box remains axis-aligned.
           // AngularFactor = BulletSharp.Math.Vector3.Zero
        };

        // Enable CCD to prevent tunneling
        body.CcdMotionThreshold = 0.1f;
        body.CcdSweptSphereRadius = 0.2f;

        return body;
    }

    private RigidBody CreateFallingCube()
    {
        var shape = new BoxShape(1.5f);
        const float mass = 100.0f;
        var startTransform = Matrix.Translation(4, 5, 4);

        shape.CalculateLocalInertia(mass, out var inertia);
        var rbInfo = new RigidBodyConstructionInfo(mass, new DefaultMotionState(startTransform), shape, inertia);

        return new RigidBody(rbInfo)
        {
            Friction = 0.8f,
            Restitution = 0.2f
        };
    }

    public void StepSimulation(float deltaTime) => World.StepSimulation(deltaTime, 10);

    public void Dispose()
    {
        PlayerBody?.Dispose();
        FallingCubeBody?.Dispose();
        foreach (var body in MapCubeBodies) body?.Dispose();
        World?.Dispose();
        _solver?.Dispose();
        _broadphase?.Dispose();
        _dispatcher?.Dispose();
        _collisionConfig?.Dispose();
    }
}