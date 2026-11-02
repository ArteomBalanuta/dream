using OpenTK.Mathematics;

namespace Dream.Client.Presentation;

// What the local player's model and the third person / isometric camera share for one frame.
public readonly record struct LocalPlayerRenderPose(Vector3 Position, Vector3 Facing, bool IsMoving);

// Turns the 64 Hz stepped body position into a per-frame position. The body only moves on
// prediction ticks, so drawing it directly steps the model by a whole tick between frames while a
// velocity-led camera moves smoothly: the two drift against each other (a sawtooth of several cm).
// This renders the body interpolated between the last two tick positions (one tick of latency),
// plus whatever the body moved since the last tick (reconciler nudges), so it never lags a
// correction. The camera follows the same position, so model and camera cannot move relative to
// each other. Facing is smoothed with an exponential on the shortest arc.
public sealed class LocalPlayerRenderSmoother
{
    public const float SnapDistance = 3f;
    public const float YawSmoothingSeconds = 0.08f;
    public const float MovingOnSpeed = 0.3f;
    public const float MovingOffSpeed = 0.15f;

    private Vector3 _previous;
    private Vector3 _current;
    private bool _hasTick;
    private bool _moving;
    private float _yaw;
    private bool _yawInitialised;

    public void Reset()
    {
        _hasTick = false;
        _moving = false;
        _yawInitialised = false;
    }

    // before/after are the body position around one prediction tick of tickSeconds.
    public void RecordTick(Vector3 before, Vector3 after, float tickSeconds)
    {
        Vector3 step = after - before;
        bool snapped = !_hasTick || step.LengthSquared > SnapDistance * SnapDistance;
        _previous = snapped ? after : before;
        _current = after;
        _hasTick = true;

        float speed = tickSeconds > 0f ? new Vector2(step.X, step.Z).Length / tickSeconds : 0f;
        if (snapped)
        {
            _moving = false;
        }
        else
        {
            _moving = _moving ? speed > MovingOffSpeed : speed > MovingOnSpeed;
        }
    }

    // alpha is the fraction of a tick elapsed since the last tick, in [0, 1).
    public LocalPlayerRenderPose Resolve(Vector3 bodyNow, float alpha, Vector3 facing, float frameSeconds)
    {
        Vector3 position = bodyNow;
        if (_hasTick)
        {
            Vector3 sinceTick = bodyNow - _current;
            if (sinceTick.LengthSquared > SnapDistance * SnapDistance)
            {
                _previous = bodyNow;
                _current = bodyNow;
                _moving = false;
            }
            else
            {
                float t = Math.Clamp(alpha, 0f, 1f);
                position = Vector3.Lerp(_previous, _current, t) + sinceTick;
            }
        }

        return new LocalPlayerRenderPose(position, SmoothFacing(facing, frameSeconds), _moving);
    }

    private Vector3 SmoothFacing(Vector3 facing, float frameSeconds)
    {
        if ((facing.X * facing.X) + (facing.Z * facing.Z) < 1e-8f)
        {
            return _yawInitialised ? FromYaw(_yaw) : facing;
        }

        float target = MathF.Atan2(facing.X, facing.Z);
        if (!_yawInitialised)
        {
            _yaw = target;
            _yawInitialised = true;
        }
        else if (frameSeconds > 0f)
        {
            float delta = WrapRadians(target - _yaw);
            float blend = 1f - MathF.Exp(-frameSeconds / YawSmoothingSeconds);
            _yaw = WrapRadians(_yaw + (delta * blend));
        }

        return FromYaw(_yaw);
    }

    private static float WrapRadians(float angle)
    {
        while (angle > MathF.PI)
        {
            angle -= MathF.Tau;
        }

        while (angle < -MathF.PI)
        {
            angle += MathF.Tau;
        }

        return angle;
    }

    private static Vector3 FromYaw(float yaw) => new(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
}
