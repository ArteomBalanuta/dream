using BulletSharp;
using BulletSharp.Math;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Vector3 = OpenTK.Mathematics.Vector3;

public class Player
{
    private readonly PhysicsIntegrator _physics;
    private readonly PlayerController _controller = new();
    private readonly PlayerMotor _motor;
    private readonly PlayerCameraRig _cameraRig;
    private readonly PlayerInteraction _interaction = new();

    public Map map;
    public RigidBody PhysicsBody { get; }
    public float Speed { get; set; } = 3.0f;
    public Camera Camera => _cameraRig.Camera;

    public Vector3 LastReportedPosition { get; private set; }
    public float CollisionReportCooldown = 0.5f;
    private float _collisionReportTimer;
    private bool _wasPrimaryUsePressed;

    public Player(Map map, PhysicsIntegrator physics, RigidBody physicsBody, Vector3 position)
    {
        this.map = map;
        _physics = physics;
        _motor = new PlayerMotor(_physics);
        _cameraRig = new PlayerCameraRig(position);
        PhysicsBody = physicsBody;
        PhysicsBody.AngularFactor = BulletSharp.Math.Vector3.Zero;
        PhysicsBody.Activate();
        PhysicsBody.ActivationState = ActivationState.DisableDeactivation;
    }

    public void Update(float deltaTime, KeyboardState keyboardState, MouseState mouseState, GameSettings settings, bool allowInput)
    {
        Vector3 currentPosition = _cameraRig.SyncFromBody(PhysicsBody, settings);
        Speed = settings.MoveSpeed;

        MovementCommand movement = _controller.CaptureMovement(Camera, keyboardState, allowInput);
        _motor.Update(PhysicsBody, currentPosition, movement, settings, allowInput);
        _cameraRig.UpdateLook(deltaTime, mouseState, allowInput, _motor.IsFrozen);

        _collisionReportTimer += deltaTime;
        if (_collisionReportTimer >= CollisionReportCooldown)
        {
            CheckAndLogCollisions();
            _collisionReportTimer = 0f;
        }

        LastReportedPosition = currentPosition;
    }

    private void CheckAndLogCollisions()
    {
        var collisions = new List<CollisionInfo>();
        var callback = new PlayerContactCallback(PhysicsBody);
        _physics.World.ContactTest(PhysicsBody, callback);

        foreach (CollisionData collision in callback.Collisions)
        {
            if (collision.CollisionObject.UserObject is (Cube cube, Vector3 cubePosition))
            {
                collisions.Add(new CollisionInfo
                {
                    Cube = cube,
                    CubeWorldPosition = cubePosition,
                    PlayerPosition = LastReportedPosition
                });
            }
        }

        if (collisions.Count == 0)
        {
            return;
        }

        Console.WriteLine("\n=== Collision Report ===");
        foreach (CollisionInfo collision in collisions)
        {
            Vector3 gridPosition = WorldToGridPosition(collision.CubeWorldPosition);
            Console.WriteLine($"Colliding with cube at: [{gridPosition.X}, {gridPosition.Y}, {gridPosition.Z}]");
            Console.WriteLine($"Cube center: {collision.CubeWorldPosition}");
            Console.WriteLine($"Player position: {collision.PlayerPosition}");
        }
        Console.WriteLine("========================");
    }

    public void PlaceCube(Map map, MouseState mouseState, bool allowInput)
    {
        PlayerInteractionCommand interaction = _controller.CaptureInteraction(mouseState, allowInput);
        _interaction.TryPlaceCube(map, interaction, Camera, PhysicsBody);
    }

    public bool ConsumePrimaryUsePressed(MouseState mouseState, bool allowInput)
    {
        bool isPressed = _controller.CaptureInteraction(mouseState, allowInput).PlaceCubePressed;
        bool triggered = isPressed && !_wasPrimaryUsePressed;
        _wasPrimaryUsePressed = isPressed;
        return triggered;
    }

    public Vector3 GetWorldPosition()
    {
        PhysicsBody.MotionState.GetWorldTransform(out Matrix transform);
        return transform.Origin.ToOpenTK();
    }

    public void SetWorldPosition(Vector3 position)
    {
        _cameraRig.Teleport(PhysicsBody, position, 0.35f);
        LastReportedPosition = position;
    }

    private static Vector3 WorldToGridPosition(Vector3 worldPosition)
    {
        return new Vector3(
            (int)Math.Floor(worldPosition.X + 1e-6f),
            (int)Math.Floor(worldPosition.Y + 1e-6f),
            (int)Math.Floor(worldPosition.Z + 1e-6f));
    }

    private struct CollisionInfo
    {
        public Cube Cube;
        public Vector3 CubeWorldPosition;
        public Vector3 PlayerPosition;
    }

    public readonly struct CollisionData
    {
        public CollisionObject CollisionObject { get; init; }
        public Vector3 ContactPoint { get; init; }
    }

    public class PlayerContactCallback : ContactResultCallback
    {
        private readonly RigidBody _playerBody;
        public List<CollisionData> Collisions { get; } = new();

        public PlayerContactCallback(RigidBody playerBody)
        {
            _playerBody = playerBody;
        }

        public override float AddSingleResult(
            ManifoldPoint cp,
            CollisionObjectWrapper colObj0,
            int partId0,
            int index0,
            CollisionObjectWrapper colObj1,
            int partId1,
            int index1)
        {
            CollisionObject otherObject = colObj0.CollisionObject == _playerBody
                ? colObj1.CollisionObject
                : colObj0.CollisionObject;

            if (otherObject != null && otherObject != _playerBody)
            {
                Collisions.Add(new CollisionData
                {
                    CollisionObject = otherObject,
                    ContactPoint = cp.PositionWorldOnB.ToOpenTK()
                });
            }

            return 1f;
        }
    }
}
