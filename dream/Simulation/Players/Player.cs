using BulletSharp;
using BulletSharp.Math;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Vector3 = OpenTK.Mathematics.Vector3;
using Dream.Client.Input;
using Dream.Client.Replication;
using Dream.Server.Rules;
using Dream.Simulation.Physics;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Simulation.Players;


public class Player : IReplicatedPlayerTarget
{
    private readonly PhysicsIntegrator _physics;
    private Map _map;
    private readonly PlayerController _controller = new();
    private readonly PlayerMotor _motor;
    private readonly PlayerCameraRig _cameraRig;
    private readonly PlayerInteraction _interaction = new();
    private readonly PlayerCombatant _combatant;
    private bool _primaryUseTriggered;

    public RigidBody PhysicsBody { get; }
    public float Speed { get; set; } = 3.0f;
    public Camera Camera => _cameraRig.Camera;
    public PlayerCombatant Combatant => _combatant;
    public Map CurrentMap => _map;

    public Vector3 LastReportedPosition { get; private set; }
    public float CollisionReportCooldown = 0.5f;
    private float _collisionReportTimer;

    public Player(
        Map map,
        PhysicsIntegrator physics,
        RigidBody physicsBody,
        Vector3 position,
        SceneEventHub? eventHub = null,
        CombatFormulaService? combatFormulaService = null)
    {
        ArgumentNullException.ThrowIfNull(physics);
        ArgumentNullException.ThrowIfNull(physicsBody);

        if (!ReferenceEquals(physics.PlayerBody, physicsBody))
        {
            throw new InvalidOperationException("Player requires the integrator registered player body.");
        }

        _map = map;
        _physics = physics;
        _motor = new PlayerMotor(_physics);
        _cameraRig = new PlayerCameraRig(position);
        _combatant = new PlayerCombatant(GetWorldPosition, eventHub, combatFormulaService: combatFormulaService);
        PhysicsBody = physicsBody;
        PhysicsBody.AngularFactor = BulletSharp.Math.Vector3.Zero;
        PhysicsBody.Activate();
        PhysicsBody.ActivationState = ActivationState.DisableDeactivation;
    }

    public void Update(float deltaTime, PlayerSimulationInput input, PlayerSimulationTuning tuning)
    {
        Vector3 currentPosition = _cameraRig.SyncFromBody(
            PhysicsBody,
            tuning.EyeHeightOffset,
            tuning.MouseSensitivity);
        Speed = tuning.MoveSpeed;

        _motor.Update(
            PhysicsBody,
            currentPosition,
            input.Movement,
            tuning,
            deltaTime);
        _cameraRig.UpdateLook(deltaTime, input.LookDelta, input.AllowLook && !_motor.IsFrozen);

        _collisionReportTimer += deltaTime;
        if (_collisionReportTimer >= CollisionReportCooldown)
        {
            CheckAndLogCollisions();
            _collisionReportTimer = 0f;
        }

        LastReportedPosition = currentPosition;
    }

    public void SyncCameraFromBody(PlayerSimulationTuning tuning)
    {
        LastReportedPosition = _cameraRig.SyncFromBody(
            PhysicsBody,
            tuning.EyeHeightOffset,
            tuning.MouseSensitivity);
    }

    public void ApplyImmediateLook(Vector2 lookDelta, float mouseSensitivity, bool allowLook)
    {
        _cameraRig.ApplyImmediateLook(mouseSensitivity, lookDelta, allowLook && !_motor.IsFrozen);
    }

    public Camera CreatePresentationCamera(float leadSeconds)
    {
        Vector3 currentPosition = GetWorldPosition();
        Vector3 eyeOffset = Camera.Position - currentPosition;
        Vector3 predictedPosition = currentPosition;
        if (leadSeconds > 0f)
        {
            Vector3 linearVelocity = PhysicsBody.LinearVelocity.ToOpenTK();
            predictedPosition += new Vector3(linearVelocity.X, 0f, linearVelocity.Z) * leadSeconds;
        }

        Camera presentationCamera = Camera.Clone();
        presentationCamera.Position = predictedPosition + eyeOffset;
        return presentationCamera;
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

    public PlayerInteractionCommand CaptureInteractionCommand(
        KeyboardState keyboardState,
        MouseState mouseState,
        bool allowInput)
    {
        return _controller.CaptureInteraction(keyboardState, mouseState, allowInput);
    }

    public bool ConsumePrimaryUsePressed(PlayerInteractionCommand interaction, float repeatIntervalSeconds, float deltaTime)
    {
        _primaryUseTriggered = _interaction.ShouldTriggerPrimaryUse(interaction, repeatIntervalSeconds, deltaTime);
        return _primaryUseTriggered;
    }

    public bool TryCreateBuildUseRequest(bool primaryUseTriggered, out BuildUseRequest request)
    {
        if (!primaryUseTriggered)
        {
            request = default;
            return false;
        }

        return PlayerInteraction.TryCreateBuildUseRequest(
            GetWorldPosition(),
            Camera.Front,
            out request);
    }

    public void BindMap(Map map)
    {
        _map = map;
    }

    public Vector3 GetWorldPosition()
    {
        PhysicsBody.MotionState.GetWorldTransform(out Matrix transform);
        return transform.Origin.ToOpenTK();
    }

    public bool IsNoclipEnabled => _motor.IsNoclipEnabled;

    public void SetWorldPosition(Vector3 position)
    {
        _cameraRig.Teleport(PhysicsBody, position, 0.35f);
        LastReportedPosition = position;
    }

    public void SetFacing(Vector3 facing)
    {
        Camera.SetFacing(facing);
    }

    public void SetHealth(int currentHealth, int maxHealth)
    {
        _combatant.Health.SetAuthoritativeState(currentHealth, maxHealth);
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
