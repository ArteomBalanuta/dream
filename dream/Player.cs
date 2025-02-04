// Player.cs
using BulletSharp.Math;
using BulletSharp;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Vector3 = OpenTK.Mathematics.Vector3;

public class Player
{
    public RigidBody PhysicsBody { get; private set; }
    public float Speed { get; set; } = 15.0f;
    public Camera Camera { get; private set; }

    public Player(RigidBody physicsBody, Vector3 position)
    {
        PhysicsBody = physicsBody;
        Camera = new Camera(position);
    }

    public void Update(float deltaTime, KeyboardState keyboardState, MouseState mouseState)
    {
        // Movement forces
        Vector3 movementForce = Vector3.Zero;

        // Get camera-relative directions
        Vector3 forward = new Vector3(Camera.Front.X, 0, Camera.Front.Z).Normalized();
        Vector3 right = Camera.Right;

        // Horizontal movement
        if (keyboardState.IsKeyDown(Keys.W)) movementForce += forward;
        if (keyboardState.IsKeyDown(Keys.S)) movementForce -= forward;
        if (keyboardState.IsKeyDown(Keys.A)) movementForce -= right;
        if (keyboardState.IsKeyDown(Keys.D)) movementForce += right;

        // Vertical movement
        if (keyboardState.IsKeyDown(Keys.Space)) movementForce += 2 * Vector3.UnitY;
        if (keyboardState.IsKeyDown(Keys.LeftShift)) movementForce -= Vector3.UnitY;

        // Apply normalized force
        if (movementForce != Vector3.Zero)
        {
            movementForce = Vector3.Normalize(movementForce) * Speed;
            PhysicsBody.ApplyCentralForce(movementForce.ToBullet());
        }

        // Update camera position from physics body
        PhysicsBody.MotionState.GetWorldTransform(out Matrix transform);
        Camera.Position = transform.Origin.ToOpenTK();
        Camera.Update(deltaTime, mouseState);
    }

    public void PlaceCube(Map map, MouseState mouseState)
    {
        if (mouseState.IsButtonDown(MouseButton.Left))
        {
            Vector3 direction = Camera.Front;
            PhysicsBody.MotionState.GetWorldTransform(out Matrix transform);
            Vector3 position = transform.Origin.ToOpenTK() + direction * 2.0f;
            map.AddCube(position);
        }
    }
}