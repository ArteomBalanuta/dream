using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;

namespace Dream.Simulation.Runtime;

// Authoritative player targets: resolving the acting player for an action and building the
// reused per-tick and per-snapshot player target lists.
public sealed partial class WorldSessionRuntime
{
    private bool TryResolveActionContext(
        PlayerSessionId sessionId,
        out AuthoritativeSessionActionContext? context)
    {
        context = null;
        if (!TryResolveAuthoritativePlayerTarget(sessionId, out ConnectedPlayer? player, out IAuthoritativePlayerTarget? playerTarget)
            || player == null
            || playerTarget == null)
        {
            return false;
        }

        context = new AuthoritativeSessionActionContext(
            player,
            playerTarget.Combatant,
            playerTarget.InteractionMode,
            playerTarget.EyePosition,
            playerTarget.Facing,
            SupportsBuildActions: true,
            SupportsCombatActions: true);
        return true;
    }

    // Lists the players in the order every per-tick consumer depends on (nearest-target ties, the
    // snapshot's actor order): the primary seat first, unless it is the unoccupied local seat while
    // a remote player is spawned, then the spawned secondary seats in ordinal session-id order.
    private void FillAuthoritativePlayerTargets(List<IAuthoritativePlayerTarget> targets, bool spawnedAndAliveOnly)
    {
        targets.Clear();
        PlayerSessionId primarySessionId = ResolvePrimaryControlledSessionId();
        if (primarySessionId != LocalPlayerSessionId || !_sessions.HasSpawnedRemotePlayer())
        {
            AddTarget(targets, GetPrimaryAuthoritativePlayerTarget(primarySessionId), spawnedAndAliveOnly);
        }

        foreach (RemoteSessionPlayerState actor in _orderedSecondaryRemotePlayers)
        {
            if (IsListedSecondary(actor))
            {
                AddTarget(targets, actor, spawnedAndAliveOnly);
            }
        }

        static void AddTarget(List<IAuthoritativePlayerTarget> targets, IAuthoritativePlayerTarget target, bool spawnedAndAliveOnly)
        {
            if (!spawnedAndAliveOnly || (target.IsSpawned && target.IsAlive))
            {
                targets.Add(target);
            }
        }
    }

    // The same players FillAuthoritativePlayerTargets lists, searched by actor id without a list.
    private bool TryFindListedPlayerTarget(int actorId, out IAuthoritativePlayerTarget? playerTarget)
    {
        PlayerSessionId primarySessionId = ResolvePrimaryControlledSessionId();
        if (Player.Combatant.Actor.Id == actorId
            && (primarySessionId != LocalPlayerSessionId || !_sessions.HasSpawnedRemotePlayer()))
        {
            playerTarget = GetPrimaryAuthoritativePlayerTarget(primarySessionId);
            return true;
        }

        foreach (RemoteSessionPlayerState actor in _orderedSecondaryRemotePlayers)
        {
            if (actor.ActorId == actorId && IsListedSecondary(actor))
            {
                playerTarget = actor;
                return true;
            }
        }

        playerTarget = null;
        return false;
    }

    private bool IsListedSecondary(RemoteSessionPlayerState actor)
    {
        return _sessions.TryGet(actor.SessionId, out ConnectedPlayer? player)
            && player != null
            && player.IsSpawned;
    }

    private AuthoritativePlayerTarget GetPrimaryAuthoritativePlayerTarget(PlayerSessionId sessionId)
    {
        if (_primaryPlayerTarget == null || _primaryPlayerTarget.SessionId != sessionId)
        {
            _primaryPlayerTarget = CreatePrimaryAuthoritativePlayerTarget(sessionId);
        }

        return _primaryPlayerTarget;
    }

    // Created once per primary session id; every delegate reads the seat's live state.
    private AuthoritativePlayerTarget CreatePrimaryAuthoritativePlayerTarget(PlayerSessionId sessionId)
    {
        return new AuthoritativePlayerTarget(
            sessionId,
            Player.Combatant.Actor.Id,
            Player.GetWorldPosition,
            () => Player.Camera.Front,
            Player.Combatant,
            () => GetSessionInteractionMode(sessionId),
            Player.SetWorldPosition,
            () => Player.Camera.Position,
            isNoclipEnabledProvider: () => Player.IsNoclipEnabled);
    }

    private bool TryResolveAuthoritativePlayerTarget(
        PlayerSessionId sessionId,
        out ConnectedPlayer? player,
        out IAuthoritativePlayerTarget? playerTarget)
    {
        playerTarget = null;
        if (!_sessions.TryGet(sessionId, out player) || player == null)
        {
            return false;
        }

        if (!player.IsLocal && !player.IsSpawned)
        {
            return false;
        }

        if (player.ActorId == Player.Combatant.Actor.Id && IsPrimaryControlledSession(sessionId))
        {
            playerTarget = GetPrimaryAuthoritativePlayerTarget(sessionId);
            return true;
        }

        if (_secondaryRemotePlayers.TryGetValue(sessionId, out RemoteSessionPlayerState? actor))
        {
            playerTarget = actor;
            return true;
        }

        return false;
    }
}
