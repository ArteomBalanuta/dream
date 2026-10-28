using BulletSharp;
using BulletSharp.Math;
using OpenTK.Mathematics;
using Vector3 = OpenTK.Mathematics.Vector3;
using System.Text;
using Dream.Multiplayer.Diagnostics;
using Dream.Simulation.Rules;
using Dream.Simulation.Physics;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Simulation.Players;


// Player owns one native-backed contact callback, shares it with its motor's grounded probe and
// disposes it once in Dispose. It never disposes PhysicsBody: whoever registered the body removes
// and disposes it (the integrator, or RemoteSessionPlayerState through RemoveSessionPlayerBody).
public class Player : IReplicatedPlayerTarget, IDisposable
{
    private readonly PhysicsIntegrator _physics;
    private Map _map;
    private readonly PlayerMotor _motor;
    private readonly PlayerCameraRig _cameraRig;
    private readonly PlayerInteraction _interaction = new();
    private readonly PlayerCombatant _combatant;
    private readonly PlayerContactCallback _contactCallback;
    private readonly List<CollisionInfo> _collisionScratch = new();
    private bool _primaryUseTriggered;
    private bool _disposed;

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

        if (!physics.IsPlayerBody(physicsBody))
        {
            throw new InvalidOperationException("Player requires a registered player body (the primary body or a session player body).");
        }

        _map = map;
        _physics = physics;
        _contactCallback = new PlayerContactCallback(physicsBody);
        _motor = new PlayerMotor(_physics, _contactCallback);
        _cameraRig = new PlayerCameraRig(position);
        _combatant = new PlayerCombatant(GetWorldPosition, eventHub, combatFormulaService: combatFormulaService);
        PhysicsBody = physicsBody;
        PhysicsBody.AngularFactor = BulletSharp.Math.Vector3.Zero;
        PhysicsBody.Activate();
        PhysicsBody.ActivationState = ActivationState.DisableDeactivation;
    }

    public void Update(float deltaTime, PlayerSimulationInput input, PlayerSimulationTuning tuning)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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

    // Returns a new camera. Per-frame callers should keep one Camera and use
    // CopyPresentationCameraTo instead.
    public Camera CreatePresentationCamera(float leadSeconds)
    {
        Camera presentationCamera = new(Camera.Position);
        CopyPresentationCameraTo(presentationCamera, leadSeconds);
        return presentationCamera;
    }

    // Allocation-free form of CreatePresentationCamera: copies the player camera into target and
    // moves it to the eye position predicted leadSeconds ahead from horizontal velocity.
    public void CopyPresentationCameraTo(Camera target, float leadSeconds)
    {
        ArgumentNullException.ThrowIfNull(target);

        Vector3 currentPosition = GetWorldPosition();
        Vector3 eyeOffset = Camera.Position - currentPosition;
        Vector3 predictedPosition = currentPosition;
        if (leadSeconds > 0f)
        {
            Vector3 linearVelocity = PhysicsBody.LinearVelocity.ToOpenTK();
            predictedPosition += new Vector3(linearVelocity.X, 0f, linearVelocity.Z) * leadSeconds;
        }

        Camera.CopyTo(target);
        target.Position = predictedPosition + eyeOffset;
    }

    // Diagnostics only. The report goes to the log and nothing reads it back, so no gameplay
    // depends on it. The Bullet contact test therefore runs only while the diagnostics log is
    // enabled (DREAM_NET_DIAGNOSTICS, or a thread capture in tests). Before MEM-11 every player
    // ran it every 0.5 s with a new list and a new native callback, and wrote to the console
    // whenever it touched a cube. When enabled, the callback and list are reused and only the
    // report text is allocated.
    private void CheckAndLogCollisions()
    {
        if (!NetworkDiagnosticsLog.IsEnabled)
        {
            return;
        }

        _contactCallback.RunContactTest(_physics.World);

        List<CollisionInfo> collisions = _collisionScratch;
        collisions.Clear();
        List<CollisionData> contacts = _contactCallback.Collisions;
        for (int i = 0; i < contacts.Count; i++)
        {
            if (contacts[i].CollisionObject.UserObject is (Cube cube, Vector3 cubePosition))
            {
                collisions.Add(new CollisionInfo
                {
                    Cube = cube,
                    CubeWorldPosition = cubePosition,
                    PlayerPosition = LastReportedPosition
                });
            }
        }

        contacts.Clear();
        if (collisions.Count > 0)
        {
            NetworkDiagnosticsLog.Write(
                CollisionReportCategory,
                collisions,
                static report => FormatCollisionReport(report));
            collisions.Clear();
        }
    }

    public const string CollisionReportCategory = "player.collisions";

    private static string FormatCollisionReport(List<CollisionInfo> collisions)
    {
        StringBuilder report = new(64 + (collisions.Count * 160));
        report.Append("\n=== Collision Report ===\n");
        foreach (CollisionInfo collision in collisions)
        {
            Vector3 gridPosition = WorldToGridPosition(collision.CubeWorldPosition);
            report.Append($"Colliding with cube at: [{gridPosition.X}, {gridPosition.Y}, {gridPosition.Z}]\n");
            report.Append($"Cube center: {collision.CubeWorldPosition}\n");
            report.Append($"Player position: {collision.PlayerPosition}\n");
        }

        report.Append("========================");
        return report.ToString();
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

    public void NudgeWorldPosition(Vector3 delta)
    {
        if (delta == Vector3.Zero)
        {
            return;
        }

        _cameraRig.Translate(PhysicsBody, delta);
        LastReportedPosition = GetWorldPosition();
    }

    public void SetWorldPosition(Vector3 position)
    {
        // Cubes frozen on the body's top would otherwise float where it stood.
        _physics.WakeRidersOfPlayerBody(PhysicsBody);
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

    // Disposes the contact callback exactly once. The body is not touched (see the class note).
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _collisionScratch.Clear();
        _contactCallback.Dispose();
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

    // Native-backed, so create one per player body and reuse it. RunContactTest clears the
    // previous results first, so a reused callback holds only the latest test's contacts.
    public class PlayerContactCallback : ContactResultCallback
    {
        private readonly RigidBody _playerBody;
        public List<CollisionData> Collisions { get; } = new();

        public RigidBody PlayerBody => _playerBody;

        public bool IsDisposed { get; private set; }

        public void RunContactTest(CollisionWorld world)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            Collisions.Clear();
            world.ContactTest(_playerBody, this);
        }

        protected override void Dispose(bool disposing)
        {
            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;
            Collisions.Clear();
            base.Dispose(disposing);
        }

        public PlayerContactCallback(RigidBody playerBody)
        {
            _playerBody = playerBody;
            // Match the player body's filter so contact tests (grounding) ignore rubble too.
            CollisionFilterGroup = (int)PhysicsIntegrator.PlayerCollisionGroup;
            CollisionFilterMask = (int)PhysicsIntegrator.PlayerCollisionMask;
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
