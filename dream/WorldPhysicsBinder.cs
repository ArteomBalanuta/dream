using BulletSharp;
using BulletSharp.Math;
using OpenTK.Mathematics;
using Vector3 = OpenTK.Mathematics.Vector3;

public class WorldPhysicsBinder
{
    private readonly PhysicsIntegrator _physics;

    public WorldPhysicsBinder(PhysicsIntegrator physics)
    {
        _physics = physics;
    }

    public void AddFloorBody(int width, int depth)
    {
        var shape = new BoxShape(width / 2f, 0.5f, depth / 2f)
        {
            Margin = 0f
        };

        var floorCenter = new BulletSharp.Math.Vector3(width / 2f, 0.5f, depth / 2f);
        var startTransform = Matrix.Translation(floorCenter);
        var motionState = new DefaultMotionState(startTransform);
        var rbInfo = new RigidBodyConstructionInfo(0f, motionState, shape, BulletSharp.Math.Vector3.Zero);

        var body = new RigidBody(rbInfo)
        {
            Friction = 0.8f,
            Restitution = 0.2f,
            AngularFactor = BulletSharp.Math.Vector3.Zero
        };

        _physics.World.AddRigidBody(body);
        _physics.MapCubeBodies.Add(body);
    }

    public void AddCubeBody(Cube cube, bool isStatic)
    {
        var shape = new BoxShape(0.5f)
        {
            Margin = 0f
        };

        float mass = isStatic ? 0f : 1f;
        Vector3 position = cube.Position;
        var startTransform = Matrix.Translation(position.ToBullet());
        var motionState = new DefaultMotionState(startTransform);
        shape.CalculateLocalInertia(mass, out var inertia);
        var rbInfo = new RigidBodyConstructionInfo(mass, motionState, shape, inertia);

        var body = new RigidBody(rbInfo)
        {
            UserObject = (cube, position),
            Friction = 0.8f,
            Restitution = 0.2f,
            AngularFactor = BulletSharp.Math.Vector3.Zero
        };

        body.SetMassProps(mass, inertia);

        _physics.World.AddRigidBody(body);
        _physics.MapCubeBodies.Add(body);
        if (!isStatic)
        {
            _physics.DynamicMapCubeBodies.Add(body);
        }
    }
}
