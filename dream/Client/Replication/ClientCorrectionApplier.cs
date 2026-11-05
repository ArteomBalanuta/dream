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

        // M8: a progression with hidden experience is the public level only; the owner's real values come
        // as ServerPlayerProgress (ApplyOwnerProgress), so applying this would overwrite them.
        if (actor.Value.Progression is { Experience: >= 0 })
        {
            PlayerProgressionSnapshot progression = actor.Value.Progression.Value;
            runtime.Player.Combatant.ApplyProgression(progression.Level, progression.Experience);
            runtime.SetServerExperienceToNextLevel(progression.ExperienceToNextLevel);
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
            // The client map lets a snap discard a replayed path that runs into a solid cell; the
            // starved ticks pair the position with the prediction it stands for (W9-2).
            long discardedBefore = reconciler.DiscardedReplays;
            ClientReconciliationResult result = reconciler.Reconcile(acknowledged, actor.Value.Position, runtime.Map, actor.Value.StarvedTicks);
            if (result.Kind == ClientReconciliationKind.Snap)
            {
                // SetWorldPosition teleports the body and zeroes its velocity. Keeping the
                // momentum used to be free: the old motor set the commanded velocity outright, so
                // the tick after a snap was back at full speed. PlayerMotor now ramps from the
                // body's own horizontal velocity (RampGroundVelocity), so a zeroed body needs v/a
                // to recover -- 0.24 s at the 4.3 m/s cruise, 0.33 s at the 6.0 m/s sprint -- and
                // falls v^2/2a behind a server that never stopped: 0.51 m at cruise, 1.00 m at
                // sprint, on every snap. It also reads HorizontalSpeed ~ 0 for those frames, so
                // the local figure replays idle -> walk -> run, the stride restart
                // GaitHysteresisSpeed exists to prevent.
                //
                // The replayed snap position already contains the unacknowledged travel, so the
                // pre-snap horizontal velocity is the state that belongs with it and is restored.
                // Vertical velocity is not: a snap may be a respawn or a teleport, and the fall it
                // was in does not survive it. A snap whose replay was discarded (a teleport, or a
                // path that ran into a solid cell: see ClientPredictionReconciler) lands on the
                // server position alone, with no unacknowledged travel on it, and keeps nothing.
                BulletSharp.Math.Vector3 preSnapVelocity = runtime.Player.PhysicsBody.LinearVelocity;
                bool replayDiscarded = reconciler.DiscardedReplays != discardedBefore;
                runtime.Player.SetWorldPosition(result.SnapPosition);
                if (!replayDiscarded)
                {
                    runtime.Player.PhysicsBody.LinearVelocity =
                        new BulletSharp.Math.Vector3(preSnapVelocity.X, 0f, preSnapVelocity.Z);
                }
            }
        }

        runtime.Player.SetHealth(actor.Value.CurrentHealth, actor.Value.MaxHealth);
    }

    // M8: the owner's level, experience and base stats, which actor snapshots no longer carry.
    public static void ApplyOwnerProgress(IClientWorldState runtime, ServerPlayerProgress progress)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(progress);

        runtime.Player.Combatant.ApplyBaseStats(progress.BaseStats.ToCombatStats());
        runtime.Player.Combatant.ApplyProgression(progress.Level, progress.Experience);
        runtime.SetServerExperienceToNextLevel(progress.ExperienceToNextLevel);
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
