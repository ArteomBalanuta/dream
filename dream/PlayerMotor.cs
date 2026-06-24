using BulletSharp;
using OpenTK.Mathematics;
using Vector3 = OpenTK.Mathematics.Vector3;

public class PlayerMotor
{
    private readonly PhysicsIntegrator _physics;
    private float _originalMass;
    private BulletSharp.Math.Vector3 _originalInertia;
    private bool _massModified;
    private bool _wasJumpPressed;
    private bool _wasFreezePressed;

    private const float PlayerHalfHeight = 0.5f;
    private const float GroundEpsilon = 0.08f;

    public PlayerMotor(PhysicsIntegrator physics)
    {
        _physics = physics;
    }

    public bool IsFrozen { get; private set; }

    public void Update(RigidBody body, Vector3 currentPosition, MovementCommand command, GameSettings settings, bool allowGameplayInput)
    {
        HandleFreezeToggle(body, command.FreezePressed);

        Vector3 velocity = body.LinearVelocity.ToOpenTK();
        if (IsFrozen)
        {
            _wasJumpPressed = command.JumpPressed;
            body.AngularFactor = BulletSharp.Math.Vector3.Zero;
            return;
        }

        if (allowGameplayInput)
        {
            if (command.WorldDirection != Vector3.Zero)
            {
                body.Activate(true);
            }

            Vector3 targetVelocity = command.WorldDirection * settings.MoveSpeed;
            body.LinearVelocity = new BulletSharp.Math.Vector3(targetVelocity.X, velocity.Y, targetVelocity.Z);

            if (command.JumpPressed && !_wasJumpPressed && IsGrounded(body, currentPosition))
            {
                body.Activate(true);
                body.LinearVelocity = new BulletSharp.Math.Vector3(
                    body.LinearVelocity.X,
                    settings.JumpVelocity,
                    body.LinearVelocity.Z);
            }
        }
        else
        {
            body.LinearVelocity = new BulletSharp.Math.Vector3(0f, velocity.Y, 0f);
        }

        _wasJumpPressed = command.JumpPressed;
        body.AngularFactor = BulletSharp.Math.Vector3.Zero;
    }

    private void HandleFreezeToggle(RigidBody body, bool freezePressed)
    {
        if (freezePressed && !_wasFreezePressed)
        {
            IsFrozen = !IsFrozen;
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

                body.Gravity = new BulletSharp.Math.Vector3(0f, -9.81f, 0f);
            }

            body.Activate(true);
        }

        _wasFreezePressed = freezePressed;
    }

    private bool IsGrounded(RigidBody body, Vector3 currentPosition)
    {
        var callback = new Player.PlayerContactCallback(body);
        _physics.World.ContactTest(body, callback);
        float playerBottom = currentPosition.Y - PlayerHalfHeight;

        return callback.Collisions.Any(collision =>
            collision.ContactPoint.Y <= playerBottom + GroundEpsilon);
    }
}
