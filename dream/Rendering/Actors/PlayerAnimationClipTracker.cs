using Dream.Rendering.Models;
using Dream.Simulation.World;

namespace Dream.Rendering.Actors;

public sealed class PlayerAnimationClipTracker
{
    private readonly Dictionary<int, string> _activeClipNamesByActorId = [];
    private readonly List<int> _staleActorIds = [];

    public bool TryApplyClip(int actorId, string clipName, Action<string> playClip)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clipName);
        ArgumentNullException.ThrowIfNull(playClip);

        if (IsActiveClip(actorId, clipName))
        {
            return false;
        }

        playClip(clipName);
        _activeClipNamesByActorId[actorId] = clipName;
        return true;
    }

    // Per-frame overload: takes the actor's instance instead of a method-group delegate, which
    // would allocate a new Action<string> on every call. A clip recorded for the actor is replayed
    // when the instance is not actually looping it (a new instance after the actor's handle was
    // released and requested again starts stopped).
    public bool TryApplyClip(int actorId, string clipName, AnimatedModelInstance instance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clipName);
        ArgumentNullException.ThrowIfNull(instance);

        if (IsActiveClip(actorId, clipName) && instance.IsPlayingLoop(clipName))
        {
            return false;
        }

        instance.PlayLoop(clipName);
        _activeClipNamesByActorId[actorId] = clipName;
        return true;
    }

    public void RemoveExcept(IReadOnlySet<int> activeActorIds)
    {
        ArgumentNullException.ThrowIfNull(activeActorIds);

        _staleActorIds.Clear();
        foreach (int actorId in _activeClipNamesByActorId.Keys)
        {
            if (!activeActorIds.Contains(actorId))
            {
                _staleActorIds.Add(actorId);
            }
        }

        for (int i = 0; i < _staleActorIds.Count; i++)
        {
            _activeClipNamesByActorId.Remove(_staleActorIds[i]);
        }

        _staleActorIds.Clear();
    }

    private bool IsActiveClip(int actorId, string clipName)
    {
        return _activeClipNamesByActorId.TryGetValue(actorId, out string? activeClipName) &&
            string.Equals(activeClipName, clipName, StringComparison.Ordinal);
    }
}
