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

    public static void ApplySnapshotToRuntime(
        WorldSessionRuntime runtime,
        ServerSnapshotEnvelope snapshot,
        string localSessionId)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(localSessionId);

        ApplySnapshotMetadata(runtime, snapshot);

        ActorSnapshot? actor = snapshot.Actors
            .FirstOrDefault(candidate =>
                candidate.Kind == CreatureKind.Player &&
                string.Equals(candidate.SessionId, localSessionId, StringComparison.Ordinal));

        if (actor == null || actor.Value.SessionId == null)
        {
            return;
        }

        if (actor.Value.CombatStats.HasValue)
        {
            runtime.Player.Combatant.ApplyBaseStats(actor.Value.CombatStats.Value.ToCombatStats());
        }

        runtime.Player.SetWorldPosition(actor.Value.Position);
        runtime.Player.SetHealth(actor.Value.CurrentHealth, actor.Value.MaxHealth);
    }

    public static void ApplyCorrectionDeltaToRuntimePlayer(
        WorldSessionRuntime runtime,
        ClientCorrectionDelta correction)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        if (correction.CorrectedPosition.HasValue)
        {
            runtime.Player.SetWorldPosition(correction.CorrectedPosition.Value);
        }
    }
}
