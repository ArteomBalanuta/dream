using Dream.Rendering.Models;
using Dream.Simulation.World;

namespace Dream.Rendering.Actors;

public sealed class PlayerAnimationClipTracker
{
    private readonly Dictionary<int, string> _activeClipNamesByActorId = [];
    private readonly List<int> _staleActorIds = [];
    private readonly Dictionary<int, (string Clip, PlayerClipMode Mode, int Serial)> _framesByActorId = [];

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

    // Forgets the actor's clip, so the next TryApplyClip plays it. Used when the actor id passes to
    // another player (the server reuses player actor ids): the new player starts from its own clip.
    public bool Remove(int actorId)
    {
        _framesByActorId.Remove(actorId);
        return _activeClipNamesByActorId.Remove(actorId);
    }

    // Applies one frame of a player's PlayerAnimationStateMachine to its instance: loops start
    // when the clip changes, a one-shot starts when its serial changes (and returns to the loop
    // that was playing), and a held clip (death) starts once and stays on its last pose. Playback
    // speed follows the frame. Allocates nothing once the actor is tracked.
    public void ApplyFrame(int actorId, in PlayerAnimationFrame frame, AnimatedModelInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        instance.AnimationSpeed = frame.PlaybackSpeed;
        bool tracked = _framesByActorId.TryGetValue(actorId, out (string Clip, PlayerClipMode Mode, int Serial) last);
        switch (frame.Mode)
        {
            case PlayerClipMode.Loop:
                if (!instance.IsPlayingLoop(frame.Clip))
                {
                    instance.PlayLoop(frame.Clip);
                }

                break;
            case PlayerClipMode.OneShot:
                if (!(tracked && last.Mode == PlayerClipMode.OneShot && last.Serial == frame.OneShotSerial))
                {
                    if (!instance.Playback.IsPlaying)
                    {
                        instance.PlayLoop(PlayerAnimationStateMachine.IdleClip);
                    }

                    instance.PlayOneShot(frame.Clip);
                }

                break;
            default:
                if (!(tracked && last.Mode == PlayerClipMode.Hold && last.Serial == frame.OneShotSerial &&
                    string.Equals(instance.CurrentClipName, frame.Clip, StringComparison.Ordinal)))
                {
                    instance.StopAnimation();
                    instance.PlayOneShot(frame.Clip);
                }

                break;
        }

        _framesByActorId[actorId] = (frame.Clip, frame.Mode, frame.OneShotSerial);
        _activeClipNamesByActorId[actorId] = frame.Clip;
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
            _framesByActorId.Remove(_staleActorIds[i]);
        }

        _staleActorIds.Clear();
    }

    private bool IsActiveClip(int actorId, string clipName)
    {
        return _activeClipNamesByActorId.TryGetValue(actorId, out string? activeClipName) &&
            string.Equals(activeClipName, clipName, StringComparison.Ordinal);
    }
}
