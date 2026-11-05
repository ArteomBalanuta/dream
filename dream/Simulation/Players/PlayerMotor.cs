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
    // "Already at the commanded velocity", for the ramp. float.Epsilon (1.4e-45) made that test
    // an exact comparison; this is the tolerance it was meant to be.
    private const float RampVelocityEpsilon = 1e-6f;
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
        Vector3 horizontal = RampGroundVelocity(
            new Vector3(velocity.X, 0f, velocity.Z),
            groundedDirection,
            command,
            tuning,
            deltaTime);
        body.LinearVelocity = new BulletSharp.Math.Vector3(horizontal.X, velocity.Y, horizontal.Z);

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

    // A session vacating the seat must not leave its noclip or freeze on the body for the next
    // occupant. Mirrors the disable branches of the two toggle handlers.
    public void ResetSeatState(RigidBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        bool wasNoclip = IsNoclipEnabled;
        if (_massModified)
        {
            body.SetMassProps(_originalMass, _originalInertia);
            _massModified = false;
        }

        IsFrozen = false;
        IsNoclipEnabled = false;
        _wasFreezePressed = false;
        _wasNoclipTogglePressed = false;
        _wasJumpPressed = false;
        body.LinearVelocity = BulletSharp.Math.Vector3.Zero;
        body.AngularVelocity = BulletSharp.Math.Vector3.Zero;
        body.ClearForces();
        body.CollisionFlags &= ~CollisionFlags.NoContactResponse;
        if (wasNoclip)
        {
            RestoreMinimumFloorClearance(body);
        }

        body.Gravity = StandardGravity;
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

    // Walk / run / sprint tier for this command. No input means no target speed; sprint overrides
    // the throttle; otherwise a partial throttle scales the run cruise down, with the walk speed
    // as the floor so a feathered stick never crawls below a walk.
    internal static float ResolveTargetSpeed(
        bool hasDirection,
        MovementCommand command,
        PlayerSimulationTuning tuning)
    {
        if (!hasDirection)
        {
            return 0f;
        }

        if (command.Sprint)
        {
            return tuning.SprintSpeed;
        }

        float throttled = tuning.MoveSpeed * Math.Clamp(command.Throttle, 0f, 1f);
        return MathF.Max(tuning.WalkSpeed, throttled);
    }

    // Moves the body's *current* horizontal velocity toward the commanded one at a fixed rate
    // instead of snapping to it. Ramping from the body rather than a motor-local field is what
    // makes a wall collision reset the ramp (the solver already zeroed the velocity we read) and
    // what keeps client prediction and the server bit-identical: same input, same state, same
    // code.
    internal static Vector3 RampGroundVelocity(
        Vector3 currentHorizontal,
        Vector3 groundedDirection,
        MovementCommand command,
        PlayerSimulationTuning tuning,
        float deltaTime)
    {
        float targetSpeed = ResolveTargetSpeed(groundedDirection != Vector3.Zero, command, tuning);
        Vector3 targetVelocity = groundedDirection * targetSpeed;

        Vector3 delta = targetVelocity - currentHorizontal;
        float distance = delta.Length;
        if (deltaTime <= 0f)
        {
            // No time passed, so no ramping happened: the velocity is left alone. Returning the
            // target here snapped straight past the ramp, which would make the motor's output
            // depend on whether one side happened to see a zero-length tick -- in the one method
            // whose contract is that the server and a predicting client stay bit-identical. Every
            // production caller passes the fixed 1/64 s, so this was unreachable; it is still the
            // wrong answer to give.
            return currentHorizontal;
        }

        if (distance <= RampVelocityEpsilon)
        {
            return targetVelocity;
        }

        // Speeding up or turning accelerates; asking for a slower (or zero) speed brakes.
        float rate = targetSpeed >= currentHorizontal.Length
            ? tuning.GroundAcceleration
            : tuning.GroundDeceleration;
        float step = rate * deltaTime;
        return step >= distance
            ? targetVelocity
            : currentHorizontal + (delta / distance * step);
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
