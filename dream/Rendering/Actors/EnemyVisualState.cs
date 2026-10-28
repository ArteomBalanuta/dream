using Dream.Rendering.Models;

namespace Dream.Rendering.Actors;

// One enemy's render-side state: its own animation instance and the clip it last asked for.
// The instance is bound when the enemy is first drawn with a loaded model (the model loads in the
// background, so the state can exist before it does) and given back to the cache's pool when the
// enemy leaves. Instances own no GL resources.
public sealed class EnemyVisualState
{
    public string? CurrentClipName { get; private set; }

    public AnimatedModelInstance? Instance { get; private set; }

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
        return instance;
    }
}
