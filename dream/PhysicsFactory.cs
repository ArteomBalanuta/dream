using BulletSharp;
using BulletSharp.Math;

public class PhysicsFactory
{
    // Factory only creates the body; registration and ownership stay with the caller.
    public RigidBody CreatePlayerBody(Vector3 position)
    {
        var shape = new BoxShape(0.5f, 0.5f, 0.5f);
        const float mass = 1.0f;
        var startTransform = Matrix.Translation(position);
        var motionState = new DefaultMotionState(startTransform);

        shape.CalculateLocalInertia(mass, out var inertia);
        var rbInfo = new RigidBodyConstructionInfo(mass, motionState, shape, inertia);

        var body = new RigidBody(rbInfo)
        {
            Friction = 0.5f,
            Restitution = 0.0f
        };

        body.CcdMotionThreshold = 0.1f;
        body.CcdSweptSphereRadius = 0.2f;
        return body;
    }
}
