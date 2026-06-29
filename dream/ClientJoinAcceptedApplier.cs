public static class ClientJoinAcceptedApplier
{
    public static void ApplyToRuntime(WorldSessionRuntime runtime, JoinAcceptedMessage joinAccepted)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        if (!joinAccepted.InitialWorld.HasValue)
        {
            return;
        }

        ServerSnapshotEnvelope bootstrapSnapshot = new(
            joinAccepted.CurrentServerTick,
            new Dictionary<string, ulong>(),
            Array.Empty<ActorSnapshot>(),
            runtime.InteractionMode,
            joinAccepted.InitialWorld.Value,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());

        RemoteWorldMutationApplier.ApplySnapshotToRuntime(runtime, bootstrapSnapshot);
    }

    public static void ApplyToPresenter(ClientScenePresenter presenter, JoinAcceptedMessage joinAccepted)
    {
        ArgumentNullException.ThrowIfNull(presenter);
        presenter.ApplyJoinAccepted(joinAccepted);
    }
}
