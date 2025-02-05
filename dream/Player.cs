// Player.cs
using BulletSharp.Math;
using BulletSharp;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Vector3 = OpenTK.Mathematics.Vector3;

public class Player
{
    private readonly PhysicsIntegrator _physics; // Reference to the physics integrator

    public Map map;
    public RigidBody PhysicsBody { get; private set; }
    public float Speed { get; set; } = 3.0f;
    public Camera Camera { get; private set; }

    private bool isFrozen = false;


    // New properties for collision reporting
    public Vector3 LastReportedPosition { get; private set; }
    public float CollisionReportCooldown = 0.5f;
    private float _collisionReportTimer;

    public Player(Map map, PhysicsIntegrator physics, RigidBody physicsBody, Vector3 position)
    {
        this.map = map;
        _physics = physics;
        PhysicsBody = physicsBody;
        PhysicsBody.AngularFactor = BulletSharp.Math.Vector3.Zero;
        Camera = new Camera(position);

        // Optionally disable deactivation to avoid sleeping issues:
        // PhysicsBody.SetActivationState(ActivationState.DISABLE_DEACTIVATION);
    }

    public void Update(float deltaTime, KeyboardState keyboardState, MouseState mouseState)
    {
        PhysicsBody.Activate();

        // Get current position from the physics body and update camera position.
        PhysicsBody.MotionState.GetWorldTransform(out Matrix transform);
        Vector3 currentPos = transform.Origin.ToOpenTK();

        // Center the camera at the collision box center.
        // If you want an offset, e.g., to simulate an eye-level view, add an offset:
        // float offsetY = 0.25f;
        // Camera.Position = currentPos + new Vector3(0, offsetY, 0);
        Camera.Position = currentPos + new Vector3(-0.5f, -0.5f, -0.5f);

        // Calculate movement direction based on the camera’s orientation.
        Vector3 forward = new Vector3(Camera.Front.X, 0, Camera.Front.Z).Normalized();
        Vector3 right = Camera.Right;
        Vector3 movementDirection = Vector3.Zero;

        if (keyboardState.IsKeyDown(Keys.W)) movementDirection += forward;
        if (keyboardState.IsKeyDown(Keys.S)) movementDirection -= forward;
        if (keyboardState.IsKeyDown(Keys.A)) movementDirection -= right;
        if (keyboardState.IsKeyDown(Keys.D)) movementDirection += right;

        if (movementDirection != Vector3.Zero)
            movementDirection = movementDirection.Normalized();

        Vector3 targetVelocity = movementDirection * Speed;
        Vector3 currentVelocity = PhysicsBody.LinearVelocity.ToOpenTK();

        // Apply velocity on X/Z axes only.
        Vector3 impulse = new Vector3(
            targetVelocity.X - currentVelocity.X,
            0,
            targetVelocity.Z - currentVelocity.Z
        ) * deltaTime * 10;
        PhysicsBody.ApplyCentralImpulse(impulse.ToBullet());

        // Jumping.
        if (keyboardState.IsKeyDown(Keys.Space))
        {
            PhysicsBody.ApplyCentralImpulse(new BulletSharp.Math.Vector3(0, 0.5f, 0));
        }

        // Clamp horizontal speed.
        Vector3 velocity = PhysicsBody.LinearVelocity.ToOpenTK();
        Vector2 horizontalVelocity = new Vector2(velocity.X, velocity.Z);
        if (horizontalVelocity.Length > Speed)
        {
            horizontalVelocity = horizontalVelocity.Normalized() * Speed;
            PhysicsBody.LinearVelocity = new BulletSharp.Math.Vector3(
                horizontalVelocity.X,
                velocity.Y,
                horizontalVelocity.Y
            );
        }

        // Toggle freeze functionality.
        if (keyboardState.IsKeyDown(Keys.F))
        {
            isFrozen = !isFrozen;
            if (isFrozen)
            {
                PhysicsBody.Gravity = BulletSharp.Math.Vector3.Zero;
                PhysicsBody.LinearVelocity = BulletSharp.Math.Vector3.Zero;
                PhysicsBody.AngularVelocity = BulletSharp.Math.Vector3.Zero;
                PhysicsBody.SetMassProps(0, BulletSharp.Math.Vector3.Zero);
                Console.WriteLine("Player is now frozen.");
            }
            else
            {
                PhysicsBody.Gravity = new BulletSharp.Math.Vector3(0, -9.81f, 0);
                Console.WriteLine("Player is now unfrozen.");
            }
        }

        // Prevent rotation.
        PhysicsBody.AngularFactor = BulletSharp.Math.Vector3.Zero;

        // Update camera orientation based on mouse input.
        Camera.Update(deltaTime, mouseState);

        // Collision reporting logic (using a cooldown).
        _collisionReportTimer += deltaTime;
        if (_collisionReportTimer >= CollisionReportCooldown)
        {
            CheckAndLogCollisions();
            _collisionReportTimer = 0;
        }

        // Save the current position.
        LastReportedPosition = currentPos;
    }



    private void CheckAndLogCollisions()
    {
        var collisions = new List<CollisionInfo>();
        var callback = new PlayerContactCallback(PhysicsBody);
        _physics.World.ContactTest(PhysicsBody, callback);

        foreach (var collision in callback.Collisions)
        {
            // Ensure the UserObject contains a cube and its position
            if (collision.CollisionObject.UserObject is (Cube cube, Vector3 cubePos))
            {
                collisions.Add(new CollisionInfo
                {
                    Cube = cube,
                    CubeWorldPosition = cubePos,
                    PlayerPosition = LastReportedPosition,
                });
            }
        }

        if (collisions.Count > 0)
        {
            Console.WriteLine("\n=== Collision Report ===");
            foreach (var collision in collisions)
            {
                Vector3 gridPos = WorldToGridPosition(collision.CubeWorldPosition);
                Console.WriteLine($"Colliding with cube at: [{gridPos.X}, {gridPos.Y}, {gridPos.Z}]");
                Console.WriteLine($"Cube center: {collision.CubeWorldPosition}");
                Console.WriteLine($"Player position: {collision.PlayerPosition}");

                // Log nearby cubes
               // LogNearbyCubes(gridPos, 2);
            }
            Console.WriteLine("========================");
        }
    }

    // New method to log nearby cubes within a given radius
    private void LogNearbyCubes(Vector3 gridPos, int radius)
    {
        Console.WriteLine($"\n--- Nearby Cubes (radius {radius}) ---");
        for (int x = (int)gridPos.X - radius; x <= (int)gridPos.X + radius; x++)
        {
            for (int y = (int)gridPos.Y - radius; y <= (int)gridPos.Y + radius; y++)
            {
                for (int z = (int)gridPos.Z - radius; z <= (int)gridPos.Z + radius; z++)
                {
                    Vector3 pos = new Vector3(x, y, z);
                    if (map.IsInBounds(pos))
                    {
                        Cube cube = map.Cubes[x, y, z];
                        if (cube != null)
                        {
                            Console.WriteLine($"Cube at [{x}, {y}, {z}]: Solid={cube.IsSolid}, Empty={cube.IsEmpty}");

                            if (cube.IsSolid)
                            {
                                // Check if physics body exists
                                bool hasBody = _physics.MapCubeBodies.Any(b =>
                                    b.UserObject is (Cube c, Vector3 p) && c == cube);
                                Console.WriteLine($"Cube at [{x}, {y}, {z}]: PhysicsBody={hasBody}");
                            }
                        }
                        else
                        {
                            Console.WriteLine($"Cube at [{x}, {y}, {z}]: NULL (potential hole)");
                        }

                        
                    }
                    else
                    {
                        Console.WriteLine($"Cube at [{x}, {y}, {z}]: Out-of-bounds");
                    }
                }
            }
        }
        Console.WriteLine("--------------------------------------");
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

    private Vector3 WorldToGridPosition(Vector3 worldPos)
    {
        // Snap to grid with epsilon to handle floating-point precision
        return new Vector3(
            (int)Math.Floor(worldPos.X + 1e-6f),
            (int)Math.Floor(worldPos.Y + 1e-6f),
            (int)Math.Floor(worldPos.Z + 1e-6f)
        );
    }

    private struct CollisionInfo
    {
        public Cube Cube;
        public Vector3 CubeWorldPosition;
        public Vector3 PlayerPosition;
    }

    private struct CollisionData
    {
        public CollisionObject CollisionObject;
        public Vector3 ContactPoint;
    }

    private class PlayerContactCallback : ContactResultCallback
    {
        private readonly RigidBody _playerBody;
        public List<CollisionData> Collisions { get; } = new List<CollisionData>();

        public PlayerContactCallback(RigidBody playerBody)
        {
            _playerBody = playerBody;
        }

        public override float AddSingleResult(ManifoldPoint cp,
            CollisionObjectWrapper colObj0, int partId0, int index0,
            CollisionObjectWrapper colObj1, int partId1, int index1)
        {
            // Determine the other object (not the player)
            var otherObj = colObj0.CollisionObject == _playerBody
                ? colObj1.CollisionObject
                : colObj0.CollisionObject;

            if (otherObj != null && otherObj != _playerBody)
            {
                Collisions.Add(new CollisionData
                {
                    CollisionObject = otherObj,
                    ContactPoint = cp.PositionWorldOnB.ToOpenTK()
                });
            }
            return 1;
        }
    }
}
