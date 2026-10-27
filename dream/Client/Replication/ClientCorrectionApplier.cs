using Dream.Multiplayer.Protocol;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Client.Replication;

public static class ClientCorrectionApplier
{
    public static void ApplySnapshotMetadata(
        IReplicatedSessionStateTarget target,
        ServerSnapshotEnvelope snapshot)
    {
        ArgumentNullException.ThrowIfNull(target);

        target.SetAuthoritativeInteractionMode(snapshot.InteractionMode);
    }

    public static void ApplyActorSnapshot(IReplicatedPlayerTarget target, ActorSnapshot actor)
    {
        ArgumentNullException.ThrowIfNull(target);

        target.SetWorldPosition(actor.Position);
        target.SetFacing(actor.Facing);
        target.SetHealth(actor.CurrentHealth, actor.MaxHealth);
    }

    public static void ApplyCorrectionDelta(IReplicatedPlayerTarget target, ClientCorrectionDelta correction)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (correction.CorrectedPosition.HasValue)
        {
            target.SetWorldPosition(correction.CorrectedPosition.Value);
        }

        if (correction.CorrectedFacing.HasValue)
        {
            target.SetFacing(correction.CorrectedFacing.Value);
        }
    }

    // Without a reconciler the local player is hard-snapped to the snapshot position, which is
    // right for the join bootstrap. Connected play passes a reconciler so normal snapshots only
    // correct real divergence instead of pulling the player back by the latency gap.
    public static void ApplySnapshotToRuntime(
        IClientWorldState runtime,
        ServerSnapshotEnvelope snapshot,
        string localSessionId,
        ClientPredictionReconciler? reconciler = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(localSessionId);

        ActorSnapshot? actor = FindLocalPlayer(snapshot.Actors, localSessionId);
        if (actor == null || actor.Value.SessionId == null)
        {
            ApplySnapshotMetadata(runtime, snapshot);
            return;
        }

        runtime.SetAuthoritativeInteractionMode(actor.Value.InteractionMode ?? snapshot.InteractionMode);

        if (actor.Value.CombatStats.HasValue)
        {
            runtime.Player.Combatant.ApplyBaseStats(actor.Value.CombatStats.Value.ToCombatStats());
        }

        if (actor.Value.Progression.HasValue)
        {
            PlayerProgressionSnapshot progression = actor.Value.Progression.Value;
            runtime.Player.Combatant.ApplyProgression(progression.Level, progression.Experience);
        }

        if (reconciler == null)
        {
            runtime.Player.SetWorldPosition(actor.Value.Position);
        }
        else
        {
            ulong? acknowledged = snapshot.LastAcceptedCommandSequences.TryGetValue(localSessionId, out ulong sequence)
                ? sequence
                : null;
            ClientReconciliationResult result = reconciler.Reconcile(acknowledged, actor.Value.Position);
            if (result.Kind == ClientReconciliationKind.Snap)
            {
                runtime.Player.SetWorldPosition(result.SnapPosition);
            }
        }

        runtime.Player.SetHealth(actor.Value.CurrentHealth, actor.Value.MaxHealth);
    }

    public static void ApplyCorrectionDeltaToRuntimePlayer(
        IClientWorldState runtime,
        ClientCorrectionDelta correction)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        if (correction.CorrectedPosition.HasValue)
        {
            runtime.Player.SetWorldPosition(correction.CorrectedPosition.Value);
        }
    }

    // The first player actor of the local session, as FirstOrDefault returned it, without the
    // closure and boxed enumerator that cost on every snapshot.
    private static ActorSnapshot? FindLocalPlayer(IReadOnlyList<ActorSnapshot> actors, string localSessionId)
    {
        for (int i = 0; i < actors.Count; i++)
        {
            ActorSnapshot candidate = actors[i];
            if (candidate.Kind == CreatureKind.Player &&
                string.Equals(candidate.SessionId, localSessionId, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }
}
