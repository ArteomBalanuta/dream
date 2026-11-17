using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;

namespace Dream.Simulation.Runtime;

// The authoritative update loop: Update, AdvanceAuthoritativeTick, queued-action processing and
// secondary remote session advancement.
public sealed partial class WorldSessionRuntime
{
    // Receive order, then ordinal session id, then action sequence: the order of the stable LINQ
    // OrderBy/ThenBy chain this replaces. The position in the caller's list breaks full ties the
    // way that stable sort did.
    private static readonly Comparison<(QueuedPlayerAction Action, int Index)> CompareQueuedActions =
        static (left, right) =>
        {
            int order = left.Action.ReceiveOrder.CompareTo(right.Action.ReceiveOrder);
            if (order != 0)
            {
                return order;
            }

            order = string.CompareOrdinal(left.Action.SessionId.Value, right.Action.SessionId.Value);
            if (order != 0)
            {
                return order;
            }

            order = left.Action.Message.ActionSequence.CompareTo(right.Action.Message.ActionSequence);
            return order != 0 ? order : left.Index.CompareTo(right.Index);
        };

    public WorldSessionUpdateResult Update(WorldSessionUpdateContext context)
    {
        if (!_sessions.TryAcceptCommand(context.CommandFrame.SessionId, context.CommandFrame.Sequence, out _))
        {
            return new WorldSessionUpdateResult(false);
        }

        _lastAppliedCommandFrame = context.CommandFrame;
        _primaryTicksSinceCommand = 0;
        ReleaseSpawnProtectionHold(
            context.CommandFrame,
            _sessions.TryGet(context.CommandFrame.SessionId, out ConnectedPlayer? heldLocal) ? heldLocal : null);
        return UpdateAcceptedCommand(
            context.DeltaTime,
            context.CommandFrame,
            context.PlayerTuning,
            context.AllowGameplaySimulation,
            deferPrimarySeatUse: false);
    }

    public WorldSessionUpdateResult AdvanceAuthoritativeTick(
        ulong serverTick,
        float deltaTime,
        PlayerCommandFrame commandFrame,
        PlayerSimulationTuning playerTuning,
        bool allowGameplaySimulation = true,
        bool deferPrimarySeatUse = false)
    {
        return AdvanceAuthoritativeTick(
            serverTick,
            deltaTime,
            (PlayerCommandFrame?)commandFrame,
            playerTuning,
            allowGameplaySimulation,
            deferPrimarySeatUse);
    }

    public IReadOnlyList<AuthoritativeActionResult> ProcessQueuedAuthoritativeActions(
        ulong serverTick,
        IReadOnlyList<QueuedPlayerAction> queuedActions)
    {
        CurrentServerTick = serverTick;
        if (queuedActions.Count == 0)
        {
            return Array.Empty<AuthoritativeActionResult>();
        }

        _orderedQueuedActions.Clear();
        for (int i = 0; i < queuedActions.Count; i++)
        {
            _orderedQueuedActions.Add((queuedActions[i], i));
        }

        _orderedQueuedActions.Sort(CompareQueuedActions);

        // The results go back to the caller and on to the clients, so they get a fresh array.
        AuthoritativeActionResult[] results = new AuthoritativeActionResult[_orderedQueuedActions.Count];
        for (int i = 0; i < results.Length; i++)
        {
            results[i] = ProcessAuthoritativeAction(_orderedQueuedActions[i].Action.Message);
        }

        _orderedQueuedActions.Clear();
        return results;
    }

    public WorldSessionUpdateResult AdvanceAuthoritativeTick(
        ulong serverTick,
        float deltaTime,
        PlayerCommandFrame? commandFrame,
        PlayerSimulationTuning playerTuning,
        bool allowGameplaySimulation = true,
        bool deferPrimarySeatUse = false)
    {
        CurrentServerTick = serverTick;
        WorldClock.Advance();

        if (commandFrame.HasValue
            && _sessions.TryAcceptCommand(commandFrame.Value.SessionId, commandFrame.Value.Sequence, out _))
        {
            _lastAppliedCommandFrame = commandFrame.Value;
            _primaryTicksSinceCommand = 0;
            ReleaseSpawnProtectionHold(
                commandFrame.Value,
                _sessions.TryGet(commandFrame.Value.SessionId, out ConnectedPlayer? heldPrimary) ? heldPrimary : null);
        }

        // A starved primary seat repeats its last command like a secondary seat does, then decays to idle.
        PlayerCommandFrame simulatedFrame = _primaryTicksSinceCommand > MaxCommandRepeatTicks
            ? CreateStarvedCommandFrame(_lastAppliedCommandFrame)
            : _lastAppliedCommandFrame;
        if (_primaryTicksSinceCommand < int.MaxValue)
        {
            _primaryTicksSinceCommand++;
        }

        return UpdateAcceptedCommand(
            deltaTime,
            simulatedFrame,
            playerTuning,
            allowGameplaySimulation,
            deferPrimarySeatUse);
    }

    public void AdvanceSecondaryRemoteSession(
        PlayerCommandFrame commandFrame,
        float deltaTime,
        PlayerSimulationTuning playerTuning)
    {
        if (!_sessions.TryAcceptCommand(commandFrame.SessionId, commandFrame.Sequence, out ConnectedPlayer? player)
            || player == null)
        {
            return;
        }

        if (!_secondaryRemotePlayers.TryGetValue(commandFrame.SessionId, out RemoteSessionPlayerState? actor))
        {
            return;
        }

        ReleaseSpawnProtectionHold(commandFrame, player);

        if (commandFrame.RequestedMode.Mode.HasValue)
        {
            SetSessionInteractionMode(commandFrame.SessionId, commandFrame.RequestedMode.Mode.Value);
        }

        // Movement is simulated in the next tick before the physics step, together with every
        // other session; deltaTime and tuning are applied there.
        _ = deltaTime;
        _ = playerTuning;
        actor.ApplyCommand(commandFrame);
    }
}
