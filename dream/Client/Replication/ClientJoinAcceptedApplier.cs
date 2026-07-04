using Dream.Client.Presentation;
using Dream.Multiplayer.Protocol;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Client.Replication;

public static class ClientJoinAcceptedApplier
{
    public static void ApplyToRuntime(WorldSessionRuntime runtime, JoinAcceptedMessage joinAccepted)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        if (!joinAccepted.InitialWorld.HasValue && joinAccepted.EffectiveInitialActors.Count == 0)
        {
            return;
        }

        ServerSnapshotEnvelope bootstrapSnapshot = new(
            joinAccepted.CurrentServerTick,
            new Dictionary<string, ulong>(),
            joinAccepted.EffectiveInitialActors,
            runtime.InteractionMode,
            joinAccepted.InitialWorld,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());

        RemoteWorldMutationApplier.ApplySnapshotToRuntime(runtime, bootstrapSnapshot);
        ClientCorrectionApplier.ApplySnapshotToRuntime(runtime, bootstrapSnapshot, joinAccepted.SessionId);
    }

    public static void ApplyToPresenter(ClientScenePresenter presenter, JoinAcceptedMessage joinAccepted)
    {
        ArgumentNullException.ThrowIfNull(presenter);
        presenter.ApplyJoinAccepted(joinAccepted);
    }
}
