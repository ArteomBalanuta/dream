using BulletSharp;
using BulletSharp.Math;
using OpenTK.Mathematics;
using Vector3 = OpenTK.Mathematics.Vector3;
using Dream.Simulation.Physics;

namespace Dream.Simulation.Players;


public class PlayerMotor
{
    private readonly PhysicsIntegrator _physics;
    private static readonly BulletSharp.Math.Vector3 StandardGravity = new(0f, -9.81f, 0f);
    private float _originalMass;
    private BulletSharp.Math.Vector3 _originalInertia;
    private bool _massModified;
    private bool _wasJumpPressed;
    private bool _wasFreezePressed;
    private bool _wasNoclipTogglePressed;

    private const float PlayerHalfHeight = 0.5f;
    private const float GroundEpsilon = 0.08f;
    private const float WorldFloorTopY = 1f;
    private const float MinimumWorldFloorCenterY = WorldFloorTopY + PlayerHalfHeight;

    // The grounded probe reuses one native-backed contact callback. A Player passes in its own
    // and disposes it. A standalone motor (tests) creates one on first use and owns it; it is
    // replaced, and the old one disposed, only if a different body is probed. The motor is not
    // IDisposable, so a standalone motor's last callback is left to BulletSharp's finalizer.
    private Player.PlayerContactCallback? _contactCallback;
    private bool _ownsContactCallback;

    public PlayerMotor(PhysicsIntegrator physics)
    {
        _physics = physics;
    }

    internal PlayerMotor(PhysicsIntegrator physics, Player.PlayerContactCallback contactCallback)
    {
        _physics = physics;
        _contactCallback = contactCallback ?? throw new ArgumentNullException(nameof(contactCallback));
    }

    public bool IsFrozen { get; private set; }

    public bool IsNoclipEnabled { get; private set; }

    public void Update(
        RigidBody body,
        Vector3 currentPosition,
        MovementCommand command,
        PlayerSimulationTuning tuning,
        float deltaTime)
    {
        HandleFreezeToggle(body, command.FreezePressed);
        HandleNoclipToggle(body, command.ToggleNoclipPressed);

        Vector3 velocity = body.LinearVelocity.ToOpenTK();
        if (IsFrozen)
        {
            _wasJumpPressed = command.JumpPressed;
            body.AngularFactor = BulletSharp.Math.Vector3.Zero;
            return;
        }

        if (IsNoclipEnabled)
        {
            UpdateNoclip(body, currentPosition, command, tuning.NoclipSpeed, deltaTime);
            _wasJumpPressed = command.JumpPressed;
            body.AngularFactor = BulletSharp.Math.Vector3.Zero;
            return;
        }

        if (command.WorldDirection != Vector3.Zero)
        {
            body.Activate(true);
        }

        Vector3 groundedDirection = ResolveGroundedDirection(command.WorldDirection);
        Vector3 targetVelocity = groundedDirection * tuning.MoveSpeed;
        body.LinearVelocity = new BulletSharp.Math.Vector3(targetVelocity.X, velocity.Y, targetVelocity.Z);

        if (command.JumpPressed && !_wasJumpPressed && IsGrounded(body, currentPosition))
        {
            body.Activate(true);
            body.LinearVelocity = new BulletSharp.Math.Vector3(
                body.LinearVelocity.X,
                tuning.JumpVelocity,
                body.LinearVelocity.Z);
        }

        _wasJumpPressed = command.JumpPressed;
        body.AngularFactor = BulletSharp.Math.Vector3.Zero;
    }

    private void UpdateNoclip(
        RigidBody body,
        Vector3 currentPosition,
        MovementCommand command,
        float noclipSpeed,
        float deltaTime)
    {
        Vector3 movement = command.WorldDirection + (Vector3.UnitY * command.VerticalDirection);
        if (movement != Vector3.Zero)
        {
            movement = movement.Normalized();
        }

        Vector3 targetPosition = currentPosition + (movement * noclipSpeed * deltaTime);
        Matrix transform = Matrix.Translation(targetPosition.ToBullet());
        body.WorldTransform = transform;
        body.MotionState.SetWorldTransform(ref transform);
        body.LinearVelocity = BulletSharp.Math.Vector3.Zero;
        body.AngularVelocity = BulletSharp.Math.Vector3.Zero;
        body.ClearForces();
        body.Activate(true);
    }

    private void HandleFreezeToggle(RigidBody body, bool freezePressed)
    {
        bool nextFrozen = ResolveFrozenState(IsFrozen, freezePressed, _wasFreezePressed);
        if (nextFrozen != IsFrozen)
        {
            IsFrozen = nextFrozen;
            if (IsFrozen)
            {
                _originalMass = body.InvMass;
                _originalInertia = body.LocalInertia;
                body.Gravity = BulletSharp.Math.Vector3.Zero;
                body.LinearVelocity = BulletSharp.Math.Vector3.Zero;
                body.AngularVelocity = BulletSharp.Math.Vector3.Zero;
                body.SetMassProps(0f, BulletSharp.Math.Vector3.Zero);
                _massModified = true;
            }
            else
            {
                if (_massModified)
                {
                    body.SetMassProps(_originalMass, _originalInertia);
                    _massModified = false;
                }

                if (!IsNoclipEnabled)
                {
                    body.Gravity = StandardGravity;
                }
            }

            body.Activate(true);
        }

        _wasFreezePressed = freezePressed;
    }

    private void HandleNoclipToggle(RigidBody body, bool noclipPressed)
    {
        if (noclipPressed && !_wasNoclipTogglePressed)
        {
            IsNoclipEnabled = !IsNoclipEnabled;
            body.LinearVelocity = BulletSharp.Math.Vector3.Zero;
            body.AngularVelocity = BulletSharp.Math.Vector3.Zero;
            body.ClearForces();

            if (IsNoclipEnabled)
            {
                body.Gravity = BulletSharp.Math.Vector3.Zero;
                body.CollisionFlags |= CollisionFlags.NoContactResponse;
            }
            else
            {
                body.CollisionFlags &= ~CollisionFlags.NoContactResponse;
                RestoreMinimumFloorClearance(body);
                if (!IsFrozen)
                {
                    body.Gravity = StandardGravity;
                }
            }

            body.Activate(true);
        }

        _wasNoclipTogglePressed = noclipPressed;
    }

    private static void RestoreMinimumFloorClearance(RigidBody body)
    {
        body.MotionState.GetWorldTransform(out Matrix transform);
        if (transform.Origin.Y >= MinimumWorldFloorCenterY)
        {
            return;
        }

        transform.Origin = new BulletSharp.Math.Vector3(
            transform.Origin.X,
            MinimumWorldFloorCenterY,
            transform.Origin.Z);
        body.WorldTransform = transform;
        body.InterpolationWorldTransform = transform;
        body.MotionState.SetWorldTransform(ref transform);
    }

    private static Vector3 ResolveGroundedDirection(Vector3 worldDirection)
    {
        Vector3 flattened = new(worldDirection.X, 0f, worldDirection.Z);
        return flattened == Vector3.Zero
            ? Vector3.Zero
            : flattened.Normalized();
    }

    public static bool ResolveFrozenState(bool isFrozen, bool freezePressed, bool wasFreezePressed)
    {
        return freezePressed && !wasFreezePressed
            ? !isFrozen
            : isFrozen;
    }

    private bool IsGrounded(RigidBody body, Vector3 currentPosition)
    {
        Player.PlayerContactCallback callback = GetContactCallback(body);
        callback.RunContactTest(_physics.World);
        float playerBottom = currentPosition.Y - PlayerHalfHeight;

        // Same test as the former Collisions.Any(...) without its closure and enumerator.
        List<Player.CollisionData> contacts = callback.Collisions;
        bool grounded = false;
        for (int i = 0; i < contacts.Count; i++)
        {
            if (contacts[i].ContactPoint.Y <= playerBottom + GroundEpsilon)
            {
                grounded = true;
                break;
            }
        }

        contacts.Clear();
        return grounded;
    }

    private Player.PlayerContactCallback GetContactCallback(RigidBody body)
    {
        if (_contactCallback != null && ReferenceEquals(_contactCallback.PlayerBody, body))
        {
            return _contactCallback;
        }

        if (!_ownsContactCallback && _contactCallback != null)
        {
            // A Player's motor only ever probes the Player's own body.
            throw new InvalidOperationException("PlayerMotor was given a contact callback for a different body.");
        }

        _contactCallback?.Dispose();
        _contactCallback = new Player.PlayerContactCallback(body);
        _ownsContactCallback = true;
        return _contactCallback;
    }
}
