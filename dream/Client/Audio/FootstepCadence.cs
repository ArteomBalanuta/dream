using OpenTK.Mathematics;

namespace Dream.Client.Audio;

// Decides when a walking body takes a step, from the distance its drawn pose moved (not from the
// animation, which differs for every actor): one step per stride walked on the ground, a longer
// stride above the sprint speed. One instance per walker.
public sealed class FootstepCadence
{
    public const float WalkStride = 1.6f;
    public const float SprintStride = 2.2f;

    // A jump in position this big in one update is a teleport or a snap, not a walk.
    public const float MaxStepDistance = 3f;

    private readonly float _walkStride;
    private readonly float _sprintStride;
    private readonly float _sprintSpeed;
    private Vector3 _last;
    private bool _hasLast;
    private float _accumulated;

    public FootstepCadence(float walkStride = WalkStride, float sprintStride = SprintStride, float sprintSpeed = 9f)
    {
        _walkStride = walkStride;
        _sprintStride = sprintStride;
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
        float stride = speed > _sprintSpeed ? _sprintStride : _walkStride;
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
