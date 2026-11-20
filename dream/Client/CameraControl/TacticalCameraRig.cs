using OpenTK.Mathematics;

namespace Dream.Client.CameraControl;

// The tactical camera's state: a goal (pan offset, yaw, distance) and the current pose that follows it.
// Advance once per rendered frame with the unscaled frame time.
public sealed class TacticalCameraRig
{
    private const float MaxDeltaSeconds = 0.1f;
    private const float RecentreTau = 0.15f;
    private const float ReturnTau = 0.35f;
    private const float DoubleTapSeconds = 0.4f;
    private const float GestureHoldSeconds = 0.3f;
    private const float GestureSettleLog = 1e-3f;
    private const float AnchorYawToleranceDegrees = 0.5f;

    private TacticalCameraSettings _settings;
    private float _goalYaw;
    private float _currentYaw;
    private float _goalDistance;
    private float _currentDistance;
    private Vector2 _offset;        // x, z relative to the player focus
    private Vector2 _panVelocity;
    private Vector2 _worldTarget;   // Free mode only
    private CameraAnchorMode _mode;
    private bool _started;
    private bool _recentring;
    private float _clock;
    private float _lastRecentreAt = -100f;
    private float _lastNotchAt = -100f;
    private float _idleSeconds;
    private bool _gestureActive;
    private Vector3? _zoomAnchor;
    private Vector3 _lastFocus;
    private TacticalCameraPose _pose;

    public TacticalCameraRig(TacticalCameraSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings.Sanitized();
        _goalYaw = _currentYaw = _settings.DefaultYawDegrees;
        _goalDistance = _currentDistance = _settings.DefaultDistance;
        _mode = _settings.AnchorMode;
        _pose = BuildPose(Vector3.Zero);
    }

    public TacticalCameraSettings Settings
    {
        get => _settings;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _settings = value.Sanitized();
            _goalDistance = Math.Clamp(_goalDistance, _settings.MinDistance, _settings.MaxDistance);
            _currentDistance = Math.Clamp(_currentDistance, _settings.MinDistance, _settings.MaxDistance);
            ApplyLeash();
            if (_mode == CameraAnchorMode.Free)
            {
                _worldTarget = new Vector2(_lastFocus.X, _lastFocus.Z) + _offset;
            }

            _pose = BuildPose(_lastFocus);
        }
    }

    // The current pose: what renders.
    public TacticalCameraPose Pose => _pose;

    public CameraAnchorMode AnchorMode => _mode;

    // Current view target minus the player focus, Y always 0.
    public Vector3 PanOffset => new(_offset.X, 0f, _offset.Y);

    // True from a zoom gesture's first notch until the distance has settled on its goal.
    public bool ZoomGestureActive => _gestureActive;

    // Called once when a gesture starts (ZoomNotches != 0 while !ZoomGestureActive), before Advance.
    // null: zoom about the view centre for this gesture.
    public void LatchZoomAnchor(Vector3? worldAnchor)
    {
        _zoomAnchor = worldAnchor;
    }

    public TacticalCameraPose Advance(Vector3 playerFocus, in CameraInputFrame input, float realDeltaSeconds)
    {
        float dt = float.IsFinite(realDeltaSeconds) ? Math.Clamp(realDeltaSeconds, 0f, MaxDeltaSeconds) : 0f;
        _clock += dt;
        Vector2 focusXz = new(playerFocus.X, playerFocus.Z);
        if (!_started)
        {
            _started = true;
            _worldTarget = focusXz + _offset;
        }
        else if (_mode == CameraAnchorMode.Free)
        {
            _offset = _worldTarget - focusXz;
        }

        _lastFocus = playerFocus;

        // Mode toggle: the target stays where it is (the offset already describes it).
        if (input.ToggleAnchorPressed)
        {
            _mode = _mode == CameraAnchorMode.Follow ? CameraAnchorMode.Free : CameraAnchorMode.Follow;
        }

        Vector2 pan = input.EdgePan + input.KeyPan;
        if (!float.IsFinite(pan.X) || !float.IsFinite(pan.Y))
        {
            pan = Vector2.Zero;
        }

        float panLength = pan.Length;
        if (panLength > 1f)
        {
            pan /= panLength;
        }

        bool panning = pan.X != 0f || pan.Y != 0f;

        // Recentre.
        if (input.RecentrePressed)
        {
            _recentring = true;
            if (_clock - _lastRecentreAt <= DoubleTapSeconds)
            {
                _goalYaw = WrapDegrees(_settings.DefaultYawDegrees);
                _goalDistance = Math.Clamp(_settings.DefaultDistance, _settings.MinDistance, _settings.MaxDistance);
            }

            _lastRecentreAt = _clock;
        }

        // Rotation goal.
        float drag = float.IsFinite(input.RotateDragPixels) ? input.RotateDragPixels : 0f;
        if (drag != 0f)
        {
            _goalYaw = WrapDegrees(_goalYaw + drag * _settings.RotateDegreesPerPixel);
        }

        if (input.RotateDragReleased && _settings.SnapOnDragRelease)
        {
            _goalYaw = WrapDegrees(SnapToGrid(_goalYaw));
        }

        if (input.RotateSteps != 0)
        {
            _goalYaw = WrapDegrees(SnapToGrid(_goalYaw) + input.RotateSteps * _settings.RotateSnapStepDegrees);
        }

        // Zoom goal.
        float notches = float.IsFinite(input.ZoomNotches) ? input.ZoomNotches : 0f;
        if (notches != 0f)
        {
            _goalDistance = Math.Clamp(
                _goalDistance * MathF.Pow(_settings.ZoomStepRatio, -notches),
                _settings.MinDistance,
                _settings.MaxDistance);
            _lastNotchAt = _clock;
            _gestureActive = true;
        }

        // Smooth yaw along the shortest arc.
        float yawDiff = WrapDegrees(_goalYaw - _currentYaw);
        if (yawDiff != 0f)
        {
            float k = Blend(dt, _settings.RotateSmoothingSeconds);
            if (k >= 1f || MathF.Abs(yawDiff) < 1e-4f)
            {
                _currentYaw = _goalYaw;
            }
            else
            {
                _currentYaw = WrapDegrees(_currentYaw + yawDiff * k);
            }
        }

        // Smooth distance in log space.
        Vector3 previousCamera = _pose.Position;
        float previousDistance = _currentDistance;
        if (_currentDistance != _goalDistance)
        {
            float logDiff = MathF.Log(_goalDistance / _currentDistance);
            float k = Blend(dt, _settings.ZoomSmoothingSeconds);
            if (k >= 1f || MathF.Abs(logDiff) < 1e-4f)
            {
                _currentDistance = _goalDistance;
            }
            else
            {
                _currentDistance = Math.Clamp(
                    _currentDistance * MathF.Exp(logDiff * k),
                    _settings.MinDistance,
                    _settings.MaxDistance);
            }
        }

        // Zoom-to-cursor.
        if (_zoomAnchor is Vector3 anchor
            && _currentDistance != previousDistance
            && MathF.Abs(WrapDegrees(_currentYaw - _goalYaw)) <= AnchorYawToleranceDegrees
            && ZoomAnchorSolver.TrySolveTarget(
                anchor, previousCamera, _currentYaw, _settings.PitchDegrees, _currentDistance, playerFocus.Y, out Vector3 solved))
        {
            _offset = new Vector2(solved.X, solved.Z) - focusXz;
        }

        // Pan.
        if (panning)
        {
            _recentring = false;
        }

        if (_recentring)
        {
            _panVelocity = Vector2.Zero;
            _offset *= 1f - Blend(dt, RecentreTau);
            if (_offset.LengthSquared < 1e-4f)
            {
                _offset = Vector2.Zero;
                _recentring = false;
            }
        }
        else
        {
            IntegratePan(pan, dt);
        }

        // Return to the player after idling.
        if (panning || input.RotateDragActive)
        {
            _idleSeconds = 0f;
        }
        else
        {
            _idleSeconds += dt;
        }

        if (!_recentring && _settings.ReturnToPlayerSeconds > 0f && _idleSeconds >= _settings.ReturnToPlayerSeconds
            && (_offset.X != 0f || _offset.Y != 0f))
        {
            _panVelocity = Vector2.Zero;
            _offset *= 1f - Blend(dt, ReturnTau);
            if (_offset.LengthSquared < 1e-4f)
            {
                _offset = Vector2.Zero;
            }
        }

        ApplyLeash();
        _worldTarget = focusXz + _offset;

        // Gesture end.
        if (_gestureActive && notches == 0f
            && _clock - _lastNotchAt >= GestureHoldSeconds
            && MathF.Abs(MathF.Log(_currentDistance / _goalDistance)) < GestureSettleLog)
        {
            _gestureActive = false;
            _zoomAnchor = null;
        }

        _pose = BuildPose(playerFocus);
        return _pose;
    }

    // Sets goal and current together (no smoothing). A null argument keeps that value.
    public void SnapTo(Vector3 playerFocus, float? yawDegrees, float? distance, Vector2? panOffsetXz, CameraAnchorMode? mode)
    {
        if (_started && _mode == CameraAnchorMode.Free)
        {
            _offset = _worldTarget - new Vector2(_lastFocus.X, _lastFocus.Z);
        }

        _started = true;
        _lastFocus = playerFocus;
        if (yawDegrees is float yaw && float.IsFinite(yaw))
        {
            _currentYaw = WrapDegrees(yaw);
        }

        _goalYaw = _currentYaw;

        if (distance is float d && float.IsFinite(d))
        {
            _currentDistance = Math.Clamp(d, _settings.MinDistance, _settings.MaxDistance);
        }

        _goalDistance = _currentDistance;

        if (panOffsetXz is Vector2 pan && float.IsFinite(pan.X) && float.IsFinite(pan.Y))
        {
            _offset = pan;
        }

        if (mode is CameraAnchorMode m && Enum.IsDefined(m))
        {
            _mode = m;
        }

        _panVelocity = Vector2.Zero;
        _recentring = false;
        _gestureActive = false;
        _zoomAnchor = null;
        _idleSeconds = 0f;
        ApplyLeash();
        _worldTarget = new Vector2(playerFocus.X, playerFocus.Z) + _offset;
        _pose = BuildPose(playerFocus);
    }

    // Default pose on the player, zero offset, zero velocity, the settings' anchor mode.
    public void Reset(Vector3 playerFocus)
    {
        _started = true;
        _lastFocus = playerFocus;
        _goalYaw = _currentYaw = WrapDegrees(_settings.DefaultYawDegrees);
        _goalDistance = _currentDistance = Math.Clamp(_settings.DefaultDistance, _settings.MinDistance, _settings.MaxDistance);
        _offset = Vector2.Zero;
        _panVelocity = Vector2.Zero;
        _worldTarget = new Vector2(playerFocus.X, playerFocus.Z);
        _mode = _settings.AnchorMode;
        _recentring = false;
        _gestureActive = false;
        _zoomAnchor = null;
        _idleSeconds = 0f;
        _pose = BuildPose(playerFocus);
    }

    private void IntegratePan(Vector2 pan, float dt)
    {
        if (dt <= 0f)
        {
            return;
        }

        TacticalCameraPose basis = new(Vector3.Zero, _currentYaw, _settings.PitchDegrees, 1f);
        Vector3 right = basis.GroundRight;
        Vector3 forward = basis.GroundForward;
        float speed = _settings.PanSpeed * (_currentDistance / _settings.DefaultDistance);
        Vector2 goalVelocity = new(
            (right.X * pan.X + forward.X * pan.Y) * speed,
            (right.Z * pan.X + forward.Z * pan.Y) * speed);

        float tau = goalVelocity.LengthSquared > _panVelocity.LengthSquared
            ? _settings.PanAccelSeconds
            : _settings.PanDecelSeconds;

        // Exact integral of a first-order approach, so the offset does not depend on the frame rate.
        Vector2 start = _panVelocity;
        if (tau <= 0f)
        {
            _panVelocity = goalVelocity;
            _offset += goalVelocity * dt;
        }
        else
        {
            float decay = MathF.Exp(-dt / tau);
            _panVelocity = goalVelocity + (start - goalVelocity) * decay;
            _offset += goalVelocity * dt + (start - goalVelocity) * (tau * (1f - decay));
        }
    }

    private void ApplyLeash()
    {
        float length = _offset.Length;
        float leash = _settings.LeashMetres;
        if (length <= leash)
        {
            return;
        }

        Vector2 normal = _offset / length;
        _offset = normal * leash;
        float outward = Vector2.Dot(_panVelocity, normal);
        if (outward > 0f)
        {
            _panVelocity -= normal * outward;
        }
    }

    private TacticalCameraPose BuildPose(Vector3 focus) =>
        new(new Vector3(focus.X + _offset.X, focus.Y, focus.Z + _offset.Y), _currentYaw, _settings.PitchDegrees, _currentDistance);

    private float SnapToGrid(float yaw)
    {
        float origin = _settings.DefaultYawDegrees;
        float step = _settings.RotateSnapStepDegrees;
        return origin + MathF.Round(WrapDegrees(yaw - origin) / step) * step;
    }

    private static float Blend(float dt, float tau) => tau <= 0f ? 1f : 1f - MathF.Exp(-dt / tau);

    // Angle into (-180, 180].
    private static float WrapDegrees(float degrees)
    {
        if (degrees > -180f && degrees <= 180f)
        {
            return degrees;
        }

        float wrapped = degrees % 360f;
        if (wrapped > 180f)
        {
            wrapped -= 360f;
        }
        else if (wrapped <= -180f)
        {
            wrapped += 360f;
        }

        return wrapped;
    }
}
