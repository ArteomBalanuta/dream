using System.Diagnostics.CodeAnalysis;
using Dream.Rendering.Hud;
using Dream.Simulation.Combat;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Client.Replication;

// Which player session held a player actor id in the last snapshot the client applied.
// Implemented by ClientScenePresenter.
public interface IPlayerActorSessionHistory
{
    bool TryGetLastSnapshotPlayerSession(int actorId, [NotNullWhen(true)] out string? sessionId);
}

public static class ClientSnapshotDamageApplier
{
    // Must run before the history applies this snapshot, so the history still describes the
    // previous one (PlayScene applies damage before ClientScenePresenter.ApplySnapshot).
    public static void ApplyToRuntime(
        IClientWorldState runtime,
        SceneEventHub events,
        ServerSnapshotEnvelope snapshot,
        IPlayerActorSessionHistory playerSessions)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(playerSessions);

        IReadOnlyList<DamageEventDelta> damageEvents = snapshot.DamageEvents;
        for (int i = 0; i < damageEvents.Count; i++)
        {
            DamageEventDelta damageEvent = damageEvents[i];
            if (damageEvent.Amount <= 0 && !damageEvent.ActorDied)
            {
                continue;
            }

            if (!IsAttributable(damageEvent, snapshot, playerSessions))
            {
                continue;
            }

            if (!runtime.TryResolveCombatActor(damageEvent.ActorId, out CombatActorHandle actor))
            {
                continue;
            }

            if (damageEvent.Amount > 0)
            {
                events.Publish(new DamageAppliedEvent(actor, damageEvent.Amount, ResolveHitKind(damageEvent.HitKind)));
            }

            // Client combatants have no hub, so ActorDiedEvent never fires on its own; the wire's
            // ActorDied flag is the client's only death signal. The server writes two deltas per
            // death (Amount 0, and the killing hit), so publish once per actor per snapshot.
            if (damageEvent.ActorDied && !DiedEarlierInSnapshot(damageEvents, i, damageEvent.ActorId))
            {
                events.Publish(new ActorDiedEvent(actor));
            }
        }
    }

    private static bool DiedEarlierInSnapshot(IReadOnlyList<DamageEventDelta> damageEvents, int index, int actorId)
    {
        for (int i = 0; i < index; i++)
        {
            if (damageEvents[i].ActorDied && damageEvents[i].ActorId == actorId)
            {
                return true;
            }
        }

        return false;
    }

    // A value this client does not know (a newer or hostile peer) is a regular hit.
    public static FloatingCombatTextKind ResolveHitKind(FloatingCombatTextKind sent) => sent switch
    {
        FloatingCombatTextKind.Critical => FloatingCombatTextKind.Critical,
        FloatingCombatTextKind.Incoming => FloatingCombatTextKind.Incoming,
        _ => FloatingCombatTextKind.Regular
    };

    // A damage event carries an actor id but no session, and the server reuses a player actor id
    // once its session leaves. The events of one snapshot span the whole interval since the
    // previous snapshot, without their order relative to a leave or a join, so a player event
    // belongs to a known player only when the same session held the id at both ends of the
    // interval: in the previous snapshot and in this one. Any other player event (the id changed
    // hands, appeared, or vanished in the interval, or no snapshot came before it since the join)
    // could be the previous holder's, and is dropped rather than shown on the wrong player.
    // (An id that left and came back to the same session within one interval is
    // indistinguishable from one that never changed hands, and passes.) Enemy and object ids are
    // not reused this way and always pass.
    public static bool IsAttributable(
        DamageEventDelta damageEvent,
        ServerSnapshotEnvelope snapshot,
        IPlayerActorSessionHistory playerSessions)
    {
        if (damageEvent.Kind != CreatureKind.Player)
        {
            return true;
        }

        if (!playerSessions.TryGetLastSnapshotPlayerSession(damageEvent.ActorId, out string? previousSessionId))
        {
            return false;
        }

        IReadOnlyList<ActorSnapshot> actors = snapshot.Actors;
        for (int i = 0; i < actors.Count; i++)
        {
            ActorSnapshot actor = actors[i];
            if (actor.ActorId == damageEvent.ActorId && actor.Kind == CreatureKind.Player)
            {
                return string.Equals(actor.SessionId, previousSessionId, StringComparison.Ordinal);
            }
        }

        return false;
    }
}
