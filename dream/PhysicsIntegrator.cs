// PhysicsIntegrator.cs
using BulletSharp;
using BulletSharp.Math;
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

        // Create player body
        PlayerBody = CreatePlayerBody(new Vector3(5, 2, 5));
        World.AddRigidBody(PlayerBody);

        // Falling cube
        FallingCubeBody = CreateFallingCube();
        World.AddRigidBody(FallingCubeBody);
    }

    private RigidBody CreatePlayerBody(Vector3 position)
    {
        const float playerHeight = 1.8f;
        const float playerRadius = 0.3f;
        var shape = new CapsuleShape(playerRadius, playerHeight);
        var mass = 1.0f;

        var startTransform = Matrix.Translation(position); // changed here from .ToBullet() to just position
        var motionState = new DefaultMotionState(startTransform);

        shape.CalculateLocalInertia(mass, out var inertia);
        var rbInfo = new RigidBodyConstructionInfo(mass, motionState, shape, inertia);

        var body = new RigidBody(rbInfo)
        {
            Friction = 0.5f,
            Restitution = 0.0f,
            LinearFactor = new BulletSharp.Math.Vector3(1, 1, 1) // Allow full 3D movement
        };

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