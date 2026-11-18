using OpenTK.Mathematics;
using Dream.Rendering.Models;

namespace Dream.Rendering.Actors;

// The render-only afterlife of an enemy's model, so an authored death clip is seen at all.
//
// An enemy leaves the client the frame it dies: RemoteEnemyStateApplier drops an actor the snapshot
// no longer lists as live, and WorldSessionPresentationState.SelectActiveEnemies filters out one the
// client killed locally. So EnemyClipSet.Resolve(EnemyBehaviorState.Dead) was never evaluated by any
// draw and two authored death clips shipped unused.
//
// A dying model is NOT an entity and must never be mistaken for one:
//   * it lives only here - never in EnemyVisualStateCache, never in EnemyRenderer's current-id set -
//     so no gait is measured for it, nothing hit-tests, labels, sounds or spawns effects for it, and
//     EnemyVisualStateCache.PruneExcept is untouched by it;
//   * it holds no authoritative pose. Its model matrix is FROZEN at the last matrix that was
//     actually drawn, so a withheld or stale snapshot cannot move it, and no interpolation buffer is
//     kept alive on its behalf;
//   * its lifetime is bounded twice - released the frame its death clip ends, and in any case after
//     MaxLingerSeconds, so a clip that can never report itself finished cannot leak a model.
//
// Everything here is per-frame render state and feeds nothing back into the simulation.
public sealed class EnemyDeathLinger
{
    // The hard cap. The shipped death clips are 1.3 s (wolf) and shorter (hound), so the cap only
    // bites when the clip went missing under us and the instance can never report it finished.
    public const float MaxLingerSeconds = 3f;

    // The last frame an enemy was actually drawn at: enough to keep drawing its model with no actor
    // behind it. A matrix, not a pose source.
    public readonly record struct LastDrawnEnemy(
        string ModelPath,
        string ShaderName,
        string DieClip,
        Matrix4 ModelMatrix,
        Vector3 TintOffset,
        AnimatedModelSkeleton Skeleton);

    // One model still playing out its death. Mutable because the age and the end flag are stepped
    // every frame; nothing else about it ever changes after Begin.
    public sealed class DyingModel
    {
        internal DyingModel(in LastDrawnEnemy last, AnimatedModelInstance instance)
        {
            Last = last;
            Instance = instance;
        }

        public LastDrawnEnemy Last { get; }

        public AnimatedModelInstance Instance { get; }

        public float AgeSeconds { get; internal set; }

        // Set the frame the clip finished (or stopped). The model is drawn once more at that final
        // pose and released at the top of the next Advance.
        public bool ClipEnded { get; internal set; }
    }

    private readonly Dictionary<int, LastDrawnEnemy> _lastDrawn = new();
    private readonly Dictionary<int, DyingModel> _dying = new();
    private readonly List<int> _scratchIds = new();

    public int Count => _dying.Count;

    public int LastDrawnCount => _lastDrawn.Count;

    public Dictionary<int, DyingModel>.ValueCollection Models => _dying.Values;

    public bool IsDying(int actorId) => _dying.ContainsKey(actorId);

    public bool TryGetLastDrawn(int actorId, out LastDrawnEnemy last) => _lastDrawn.TryGetValue(actorId, out last);

    // Records the frame an enemy was drawn at. Any dying model under the same actor id is released
    // first: the server reuses enemy actor ids, so a respawn must not draw a corpse under itself.
    public void ObserveDrawn(int actorId, in LastDrawnEnemy last, EnemyVisualStateCache? pool = null)
    {
        Release(actorId, pool);
        _lastDrawn[actorId] = last;
    }

    // The ids that were drawn and are absent from this frame's set. On the client an enemy only
    // leaves the registry by dying (there is no interest management and nothing else removes one
    // mid-session), so a departure is a death. Fills the caller's list; allocates nothing.
    public void CollectDeparted(HashSet<int> currentIds, List<int> departed)
    {
        ArgumentNullException.ThrowIfNull(currentIds);
        ArgumentNullException.ThrowIfNull(departed);

        departed.Clear();
        foreach (int actorId in _lastDrawn.Keys)
        {
            if (!currentIds.Contains(actorId))
            {
                departed.Add(actorId);
            }
        }
    }

    // Forgets the departed actor's last frame and starts its death clip on the instance the caller
    // rented. Nothing is begun when the caller hands over no instance (its model is gone).
    public void Begin(int actorId, in LastDrawnEnemy last, AnimatedModelInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        _lastDrawn.Remove(actorId);
        instance.AnimationSpeed = 1f;
        // StopAnimation first so no idle clip is left to return to: a corpse holds its last pose
        // instead of looping back into a gait.
        instance.StopAnimation();
        instance.PlayOneShot(last.DieClip);
        _dying[actorId] = new DyingModel(in last, instance);
    }

    public void Forget(int actorId) => _lastDrawn.Remove(actorId);

    // Releases what ended last frame, then steps the rest by deltaTime. Called once per frame
    // before the dying models are drawn, so each one is drawn once at its final pose.
    public void Advance(float deltaTime, EnemyVisualStateCache? pool = null)
    {
        if (_dying.Count == 0)
        {
            return;
        }

        _scratchIds.Clear();
        foreach ((int actorId, DyingModel dying) in _dying)
        {
            if (dying.ClipEnded || dying.AgeSeconds >= MaxLingerSeconds)
            {
                _scratchIds.Add(actorId);
                continue;
            }

            dying.AgeSeconds += deltaTime;
            // Update returns true the call a one-shot finishes; a clip the skeleton no longer
            // carries stops playback instead, which the second test catches.
            if (dying.Instance.Update(deltaTime) || !dying.Instance.Playback.IsPlaying)
            {
                dying.ClipEnded = true;
            }
        }

        for (int i = 0; i < _scratchIds.Count; i++)
        {
            Release(_scratchIds[i], pool);
        }

        _scratchIds.Clear();
    }

    // Drops the dying model under this id and gives its instance back to the pool. False when the
    // id was not dying.
    public bool Release(int actorId, EnemyVisualStateCache? pool = null)
    {
        if (!_dying.Remove(actorId, out DyingModel? dying))
        {
            return false;
        }

        dying.Instance.Reset();
        pool?.ReturnInstance(dying.Instance);
        return true;
    }

    public void Clear(EnemyVisualStateCache? pool = null)
    {
        _scratchIds.Clear();
        foreach (int actorId in _dying.Keys)
        {
            _scratchIds.Add(actorId);
        }

        for (int i = 0; i < _scratchIds.Count; i++)
        {
            Release(_scratchIds[i], pool);
        }

        _scratchIds.Clear();
        _lastDrawn.Clear();
    }
}
