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

        // Create player body
        PlayerBody = CreatePlayerBody(new Vector3(5, 2, 5));
        World.AddRigidBody(PlayerBody);

        // Falling cube
        FallingCubeBody = CreateFallingCube();
        World.AddRigidBody(PlayerBody);

    }

    private RigidBody CreatePlayerBody(Vector3 position)
    {
        const float playerHeight = 2.2f; // Increased from 1.8
        const float playerRadius = 0.4f; // Increased from 0.3


        var shape = new CapsuleShape(playerRadius, playerHeight);
        var mass = 1.0f;

        // Adjust player starting position to account for capsule dimensions
        var startPosition = new Vector3(
            position.X,
            position.Y + 1.0f, // Adjust for player height
            position.Z
            );

        var startTransform = Matrix.Translation(startPosition);
        var motionState = new DefaultMotionState(startTransform);

        shape.CalculateLocalInertia(mass, out var inertia);
        var rbInfo = new RigidBodyConstructionInfo(mass, motionState, shape, inertia);

        var body = new RigidBody(rbInfo)
        {
            Friction = 0.5f,
            Restitution = 0.0f,
            LinearFactor = new BulletSharp.Math.Vector3(1, 1, 1)
        };

        // Enable continuous collision detection
        body.CcdMotionThreshold = 0.1f;
        body.CcdSweptSphereRadius = playerRadius * 1.1f;
        body.CollisionFlags |= CollisionFlags.CharacterObject;

        
        

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