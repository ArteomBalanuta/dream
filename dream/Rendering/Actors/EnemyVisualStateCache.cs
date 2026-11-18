using Dream.Rendering.Models;

namespace Dream.Rendering.Actors;

// Per-enemy visual states keyed by actor id. A state lives while its enemy is drawn, so a frame
// with the same enemies allocates nothing. When an enemy leaves, its animation instance goes to a
// small pool and the next enemy that appears reuses it (reset to a new instance's state), so a
// steady stream of spawns does not allocate poses either. The pool only keeps instances of the
// skeleton it was last used with; instances own no GL resources, so dropping one needs no dispose.
public sealed class EnemyVisualStateCache
{
    // Enough for every enemy of a busy wave; beyond this, released instances are dropped.
    public const int MaxPooledInstances = 64;

    private readonly Dictionary<int, EnemyVisualState> _states = new();
    private readonly List<int> _staleIds = new();
    private readonly Stack<AnimatedModelInstance> _pool = new();

    public int Count => _states.Count;

    public int PooledInstanceCount => _pool.Count;

    public EnemyVisualState GetOrAdd(int actorId)
    {
        if (!_states.TryGetValue(actorId, out EnemyVisualState? state))
        {
            state = new EnemyVisualState();
            _states[actorId] = state;
        }

        return state;
    }

    // The enemy's state, bound to its own instance of the skeleton (AnimatedModel.Skeleton).
    public EnemyVisualState GetOrAdd(int actorId, AnimatedModelSkeleton skeleton)
    {
        EnemyVisualState state = GetOrAdd(actorId);
        state.Bind(skeleton, this);
        return state;
    }

    public void PruneExcept(HashSet<int> currentIds)
    {
        _staleIds.Clear();
        foreach (int id in _states.Keys)
        {
            if (!currentIds.Contains(id))
            {
                _staleIds.Add(id);
            }
        }

        foreach (int id in _staleIds)
        {
            EnemyVisualState state = _states[id];
            _states.Remove(id);
            AnimatedModelInstance? instance = state.ReleaseInstance();
            if (instance is not null)
            {
                ReturnInstance(instance);
            }
        }
    }

    // Drops an enemy's state and hands its animation instance to the caller WITHOUT pooling it:
    // the caller takes ownership and gives it back with ReturnInstance when it is done (see
    // EnemyDeathLinger, which keeps a dead enemy's model going for the length of its death clip).
    // Null when the enemy had no state or no instance - which is also when it was never drawn.
    internal AnimatedModelInstance? TakeInstance(int actorId)
    {
        if (!_states.Remove(actorId, out EnemyVisualState? state))
        {
            return null;
        }

        return state.ReleaseInstance();
    }

    // A pooled instance of the skeleton, reset, or a new one.
    internal AnimatedModelInstance RentInstance(AnimatedModelSkeleton skeleton)
    {
        while (_pool.TryPop(out AnimatedModelInstance? pooled))
        {
            if (ReferenceEquals(pooled.Skeleton, skeleton))
            {
                pooled.Reset();
                return pooled;
            }

            // A skeleton from an earlier model load (ModelAssets.DisposeAll replaced it): drop it.
        }

        return new AnimatedModelInstance(skeleton);
    }

    internal void ReturnInstance(AnimatedModelInstance instance)
    {
        if (_pool.Count < MaxPooledInstances)
        {
            _pool.Push(instance);
        }
    }
}
