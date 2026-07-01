public sealed class LatestPlayerCommandInbox : IPlayerCommandInbox
{
    private readonly Dictionary<PlayerSessionId, PlayerCommandFrame> _pending = new();

    public void Enqueue(PlayerCommandFrame commandFrame)
    {
        if (_pending.TryGetValue(commandFrame.SessionId, out PlayerCommandFrame existing))
        {
            _pending[commandFrame.SessionId] = Merge(existing, commandFrame);
            return;
        }

        _pending[commandFrame.SessionId] = commandFrame;
    }

    public IReadOnlyList<PlayerCommandFrame> Drain(IEnumerable<PlayerSessionId> sessionIds)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);

        List<PlayerCommandFrame> drained = [];
        foreach (PlayerSessionId sessionId in sessionIds)
        {
            if (_pending.TryGetValue(sessionId, out PlayerCommandFrame commandFrame))
            {
                _pending.Remove(sessionId);
                drained.Add(commandFrame);
            }
        }

        return drained;
    }

    private static PlayerCommandFrame Merge(PlayerCommandFrame existing, PlayerCommandFrame incoming)
    {
        PlayerInteractionMode? requestedMode = incoming.RequestedMode.Mode ?? existing.RequestedMode.Mode;
        PlayerMoveIntent moveIntent = new(
            incoming.MoveIntent.ForwardAxis,
            incoming.MoveIntent.StrafeAxis,
            incoming.MoveIntent.VerticalAxis,
            JumpPressed: existing.MoveIntent.JumpPressed || incoming.MoveIntent.JumpPressed,
            FreezePressed: existing.MoveIntent.FreezePressed || incoming.MoveIntent.FreezePressed,
            ToggleNoclipPressed: existing.MoveIntent.ToggleNoclipPressed || incoming.MoveIntent.ToggleNoclipPressed);
        PlayerLookIntent lookIntent = new(
            existing.LookIntent.YawDelta + incoming.LookIntent.YawDelta,
            existing.LookIntent.PitchDelta + incoming.LookIntent.PitchDelta,
            incoming.LookIntent.Facing ?? existing.LookIntent.Facing);

        return new PlayerCommandFrame(
            incoming.SessionId,
            incoming.Sequence,
            new PlayerModeSwitchIntent(requestedMode),
            moveIntent,
            lookIntent,
            existing.PrimaryUsePressed || incoming.PrimaryUsePressed);
    }
}
