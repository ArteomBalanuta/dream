using OpenTK.Mathematics;

namespace Dream.Client.Audio;

// Decides when a walking body takes a step, from the distance its drawn pose moved (not from the
// animation, which differs for every actor): one step per stride covered on the ground, with a
// longer stride for each faster gait. One instance per walker.
public sealed class FootstepCadence
{
    // The strides of the three ground gaits, grounded on real cadence rather than on the distance:
    // a walk is about 1.9 steps/s and a run about 2.8, so dividing the tier's speed by its stride
    // has to land near those numbers (1.6 / 0.85 = 1.9, 4.3 / 1.5 = 2.9, 6.0 / 2.0 = 3.0). The old
    // single 1.6 m walking stride was a stride measured at a sprint and played at a walk: four
    // metres of travel between footfalls.
    public const float WalkStride = 0.85f;
    public const float RunStride = 1.5f;
    public const float SprintStride = 2.0f;

    // The gait boundaries, the same two speeds PlayerAnimationStateMachine picks its clips on, so
    // the footfalls change cadence on the frame the stride changes.
    public const float RunSpeedThreshold = 2.6f;
    public const float SprintSpeedThreshold = 5.2f;

    // A jump in position this big in one update is a teleport or a snap, not a walk.
    public const float MaxStepDistance = 3f;

    private readonly float _walkStride;
    private readonly float _runStride;
    private readonly float _sprintStride;
    private readonly float _runSpeed;
    private readonly float _sprintSpeed;
    private Vector3 _last;
    private bool _hasLast;
    private float _accumulated;

    public FootstepCadence(
        float walkStride = WalkStride,
        float runStride = RunStride,
        float sprintStride = SprintStride,
        float runSpeed = RunSpeedThreshold,
        float sprintSpeed = SprintSpeedThreshold)
    {
        _walkStride = walkStride;
        _runStride = runStride;
        _sprintStride = sprintStride;
        _runSpeed = runSpeed;
        _sprintSpeed = sprintSpeed;
    }

    // True when a foot lands this update. At most one step per call.
    public bool Advance(Vector3 position, bool grounded, float deltaSeconds)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
        {
            return false;
        }

        if (!_hasLast)
        {
            _last = position;
            _hasLast = true;
            return false;
        }

        float dx = position.X - _last.X;
        float dz = position.Z - _last.Z;
        _last = position;
        float distance = MathF.Sqrt((dx * dx) + (dz * dz));
        if (!grounded || distance > MaxStepDistance || deltaSeconds <= 0f)
        {
            // In the air or snapped: the next steps start from here.
            if (!grounded)
            {
                _accumulated = 0f;
            }

            return false;
        }

        float speed = distance / deltaSeconds;
        float stride = speed >= _sprintSpeed ? _sprintStride : speed >= _runSpeed ? _runStride : _walkStride;
        _accumulated += distance;
        if (_accumulated < stride)
        {
            return false;
        }

        _accumulated = 0f;
        return true;
    }

    public void Reset()
    {
        _hasLast = false;
        _accumulated = 0f;
    }
}
