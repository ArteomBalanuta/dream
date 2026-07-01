using Dream.Simulation.World;

namespace Dream.Rendering.Actors;

public sealed class PlayerAnimationClipTracker
{
    private readonly Dictionary<int, string> _activeClipNamesByActorId = [];

    public bool TryApplyClip(int actorId, string clipName, Action<string> playClip)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clipName);
        ArgumentNullException.ThrowIfNull(playClip);

        if (_activeClipNamesByActorId.TryGetValue(actorId, out string? activeClipName) &&
            string.Equals(activeClipName, clipName, StringComparison.Ordinal))
        {
            return false;
        }

        playClip(clipName);
        _activeClipNamesByActorId[actorId] = clipName;
        return true;
    }

    public void RemoveExcept(IReadOnlySet<int> activeActorIds)
    {
        ArgumentNullException.ThrowIfNull(activeActorIds);

        foreach (int actorId in _activeClipNamesByActorId.Keys.ToArray())
        {
            if (!activeActorIds.Contains(actorId))
            {
                _activeClipNamesByActorId.Remove(actorId);
            }
        }
    }
}
