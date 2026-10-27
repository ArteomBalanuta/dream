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

    // Per-frame overload: takes the model instead of a method-group delegate, which would
    // allocate a new Action<string> on every call.
    public bool TryApplyClip(int actorId, string clipName, AnimatedModel model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clipName);
        ArgumentNullException.ThrowIfNull(model);

        if (IsActiveClip(actorId, clipName))
        {
            return false;
        }

        model.PlayLoop(clipName);
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
