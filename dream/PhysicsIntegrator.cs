using BulletSharp.Math;
using BulletSharp;

public class PhysicsIntegrator : IDisposable
{
    public DiscreteDynamicsWorld World { get; private set; }
    /// <summary>
    /// List of physics bodies corresponding to map cubes.
    /// Their UserObject holds a Tuple&lt;int,int,int&gt; for (x,y,z) indices.
    /// </summary>
    public List<RigidBody> MapCubeBodies { get; private set; } = new List<RigidBody>();
    /// <summary>
    /// The heavy falling cube.
    /// </summary>
    public RigidBody FallingCubeBody { get; private set; }

    // Internal Bullet objects for cleanup.
    private DefaultCollisionConfiguration collisionConfig;
    private CollisionDispatcher dispatcher;
    private BroadphaseInterface broadphase;
    private SequentialImpulseConstraintSolver solver;

    /// <summary>
    /// Creates the physics world and adds bodies for all solid cubes in the map and a heavy falling cube.
    /// </summary>
    public PhysicsIntegrator(Map map)
    {
        // Initialize Bullet world.
        collisionConfig = new DefaultCollisionConfiguration();
        dispatcher = new CollisionDispatcher(collisionConfig);
        broadphase = new DbvtBroadphase();
        solver = new SequentialImpulseConstraintSolver();
        World = new DiscreteDynamicsWorld(dispatcher, broadphase, solver, collisionConfig);
        World.Gravity = new BulletSharp.Math.Vector3(0, -10, 0);

        

        // Create a heavy, falling cube.
        // For example, a cube of size 3x3x3.
        float fallingCubeHalfExtent = 1.5f;
        CollisionShape fallingCubeShape = new BoxShape(fallingCubeHalfExtent);
        float fallingCubeMass = 0; // Heavy mass
        BulletSharp.Math.Vector3 fallingCubeStart = new BulletSharp.Math.Vector3(4, 5, 4); // Starting above the floor.
        DefaultMotionState fallingCubeMotionState = new DefaultMotionState(Matrix.Translation(fallingCubeStart));
        BulletSharp.Math.Vector3 fallingCubeInertia;
        fallingCubeShape.CalculateLocalInertia(fallingCubeMass, out fallingCubeInertia);
        RigidBodyConstructionInfo fallingCubeRbInfo = new RigidBodyConstructionInfo(fallingCubeMass, fallingCubeMotionState, fallingCubeShape, fallingCubeInertia);
        FallingCubeBody = new RigidBody(fallingCubeRbInfo)
        {
            Friction = 0.8f,
            Restitution = 0.2f
        };
        World.AddRigidBody(FallingCubeBody);
    }

    /// <summary>
    /// Advances the physics simulation by deltaTime seconds.
    /// </summary>
    public void StepSimulation(float deltaTime)
    {
        World.StepSimulation(deltaTime, 10);
    }

    public void Dispose()
    {
        FallingCubeBody.Dispose();
        foreach (var body in MapCubeBodies)
        {
            body.Dispose();
        }
        World.Dispose();
        solver.Dispose();
        broadphase.Dispose();
        dispatcher.Dispose();
        collisionConfig.Dispose();
    }
}
