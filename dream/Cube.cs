

using OpenTK.Mathematics;
using BulletSharp;
using BulletSharp.Math;
using Vector3 = OpenTK.Mathematics.Vector3; // For Matrix and Vector3

public class Cube
{
    public Vector3 Position { get; set; }
    public float Side { get; } = 1.0f;
    public bool IsEmpty { get; set; } = true;
    public bool IsSolid { get; set; } = false;
    public bool IsFire { get; set; } = false;
    public AnimatedModel Animation { get; set; }
    public string ShaderName { get; set; } = "default";

    public Cube(bool isEmpty = true, bool isSolid = false)
    {
        IsEmpty = isEmpty;
        IsSolid = isSolid;
    }

    /// <summary>
    /// Creates and returns a Bullet physics rigid body for this cube.
    /// This method encapsulates all physics initialization logic:
    /// - A 1x1 cube uses a BoxShape with half extents 0.5.
    /// - If the cube is on the floor (Y == 0) its mass is set to 0 (static).
    /// - Otherwise, it gets a small dynamic mass.
    /// </summary>
    /// 
    /*
    public RigidBody CreateRigidBody()
    {
        // Create a box shape for a 1x1 cube.
        CollisionShape shape = new BoxShape(0.50f);
        shape.Margin = 0f;
        // Floor cubes (y == 0) are static; others are dynamic.
        float mass = (Position.Y == 0) ? 0f : 1f;
        BulletSharp.Math.Vector3 inertia = BulletSharp.Math.Vector3.Zero;
        if (mass != 0)
        {
            shape.CalculateLocalInertia(mass, out inertia);
        }
        // Create the motion state based on the cube's current position.
        DefaultMotionState motionState = new DefaultMotionState(Matrix.Translation(Position.ToBullet()));
        RigidBodyConstructionInfo rbInfo = new RigidBodyConstructionInfo(mass, motionState, shape, inertia);
        RigidBody body = new RigidBody(rbInfo)
        {
            Friction = 0.5f
        };
        return body;
    }
    */
}
