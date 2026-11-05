using OpenTK.Mathematics;

namespace Dream.Client.Audio;

// Decides when a walking body takes a step, from the distance its drawn pose moved (not from the
// animation, which differs for every actor): one step per stride covered on the ground, with a
// longer stride for each faster gait. One instance per walker.
public sealed class FootstepCadence
{
    // The strides of the three ground gaits, DERIVED from the authored clips rather than from a
    // guess at real cadence: each gait is a five-key cycle with two footfalls, so at the tier's
    // reference speed the body covers referenceSpeed * cycle / 2 metres between footfalls and that
    // distance IS the stride. walk 1.6 * 0.80 / 2, run 4.3 * 0.62 / 2, sprint 6.0 * 0.44 / 2.
    //
    // The first pass of this class picked the strides from a cadence figure instead and every tier
    // came out slower than its legs - the sprint by 52% - because nothing tied the numbers to the
    // clips. FootstepCadenceTests.TheStridesMatchTheAuthoredClipCadence reads the cycle lengths
    // back off the shipped player GLB and is what keeps them tied; the tier-speed parity guard in
    // PlayerAnimationStateMachineTests structurally cannot see this class of drift.
    //
    // Walk and sprint landing within a centimetre of each other is the arithmetic, not a mistake:
    // the sprint clip is 1.8x faster per cycle but the sprint speed is 3.75x the walk's, so the
    // ground covered per footfall is nearly the same and only the rate differs (1.6 / 0.64 = 2.5
    // steps/s walking against 6.0 / 1.32 = 4.5 sprinting).
    public const float WalkStride = 0.64f;
    public const float RunStride = 1.333f;
    public const float SprintStride = 1.32f;

    // The footfalls in one authored cycle: both feet land once (the five keys are
    // forward-neutral-back-neutral-forward). Shared with the test that derives the strides.
    public const int FootfallsPerCycle = 2;

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
