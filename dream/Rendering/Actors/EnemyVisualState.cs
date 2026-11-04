using OpenTK.Mathematics;
using Dream.Rendering.Models;
using Dream.Simulation.Enemies;

namespace Dream.Rendering.Actors;

// One enemy's render-side state: its own animation instance and the clip it last asked for.
// The instance is bound when the enemy is first drawn with a loaded model (the model loads in the
// background, so the state can exist before it does) and given back to the cache's pool when the
// enemy leaves. Instances own no GL resources.
public sealed class EnemyVisualState
{
    public string? CurrentClipName { get; private set; }

    public AnimatedModelInstance? Instance { get; private set; }

    // How fast this enemy is actually moving on screen, in metres per second, smoothed. The client
    // never simulates an enemy - RemoteEnemyStateApplier gives it an inert placeholder speed and
    // only BehaviorState replicates - so the gait has to be measured from the pose that is drawn.
    public float ObservedSpeed { get; private set; }

    // The EMA weight ClientScenePresenter smooths the local player's speed with; the same one here
    // so the two gaits respond alike.
    private const float SpeedSmoothing = 0.5f;

    // A step longer than this is a teleport or a snap to a fresh snapshot, not motion; counting it
    // would spike the speed into the sprint gait for a frame. FootstepCadence.MaxStepDistance.
    private const float MaxStepDistance = 3f;

    private Vector3 _lastObservedPosition;
    private bool _hasObservedPosition;
    private bool _sprinting;

    // Records this frame's drawn position and folds the horizontal speed it implies into
    // ObservedSpeed. Allocation-free; called once per enemy per frame by EnemyRenderer.
    public void ObserveMotion(Vector3 position, float deltaSeconds)
    {
        if (!_hasObservedPosition)
        {
            _lastObservedPosition = position;
            _hasObservedPosition = true;
            return;
        }

        Vector3 step = position - _lastObservedPosition;
        step.Y = 0f;
        _lastObservedPosition = position;
        if (!(deltaSeconds > 0f))
        {
            return;
        }

        float distance = step.Length;
        if (distance > MaxStepDistance)
        {
            // A snap: keep the last speed rather than let the jump become a gallop for a frame.
            return;
        }

        float sample = distance / deltaSeconds;
        ObservedSpeed += (sample - ObservedSpeed) * SpeedSmoothing;
    }

    // The speed the gait is chosen from: the observed speed, held on the side of the clip set's
    // gait boundary this enemy is already on until it has crossed by GaitHysteresisSpeed. Returning
    // a speed rather than a clip name keeps EnemyRenderState.Create a pure function of one speed,
    // and the correction only ever lands inside the clamped playback band, so the stride rate the
    // caller derives from it is the same one the raw speed would have given.
    public float ResolveGaitSpeed(in EnemyClipSet clips, EnemyBehaviorState behaviorState)
    {
        if (behaviorState != EnemyBehaviorState.Chasing)
        {
            _sprinting = false;
            return ObservedSpeed;
        }

        _sprinting = clips.ResolveSprinting(ObservedSpeed, _sprinting);
        return _sprinting
            ? MathF.Max(ObservedSpeed, clips.SprintSpeedThreshold)
            : MathF.Min(ObservedSpeed, clips.SprintSpeedThreshold - clips.GaitHysteresisSpeed);
    }

    // Binds the state to a model skeleton. Keeps the current instance when it already belongs
    // to that skeleton; otherwise takes one from the cache's pool (or creates one) and forgets the
    // recorded clip, so the next ApplyClip starts it. Allocates only when the pool is empty.
    public AnimatedModelInstance Bind(AnimatedModelSkeleton skeleton, EnemyVisualStateCache? pool = null)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        if (Instance is not null && ReferenceEquals(Instance.Skeleton, skeleton))
        {
            return Instance;
        }

        AnimatedModelInstance? previous = Instance;
        Instance = pool?.RentInstance(skeleton) ?? new AnimatedModelInstance(skeleton);
        CurrentClipName = null;
        if (previous is not null)
        {
            pool?.ReturnInstance(previous);
        }

        return Instance;
    }

    public void ApplyClip(string clipName)
    {
        AnimatedModelInstance instance = Instance
            ?? throw new InvalidOperationException("Bind the enemy state to a model before applying a clip.");
        if (string.Equals(CurrentClipName, clipName, StringComparison.Ordinal))
        {
            return;
        }

        CurrentClipName = clipName;
        instance.PlayLoop(clipName);
    }

    // Detaches the instance (for the cache's pool) and clears the recorded clip.
    internal AnimatedModelInstance? ReleaseInstance()
    {
        AnimatedModelInstance? instance = Instance;
        Instance = null;
        CurrentClipName = null;
        // A recycled state must not measure its first step against the enemy that left.
        ObservedSpeed = 0f;
        _hasObservedPosition = false;
        _sprinting = false;
        return instance;
    }
}
