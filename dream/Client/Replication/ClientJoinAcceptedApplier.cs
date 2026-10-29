using Dream.Client.Presentation;
using Dream.Multiplayer.Protocol;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Client.Replication;

public static class ClientJoinAcceptedApplier
{
    public static void ApplyToRuntime(IClientWorldState runtime, JoinAcceptedMessage joinAccepted)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        // The descriptor comes first, so the map has the server's size before any cube or snapshot
        // reaches it. A null World (a server that does not send one) keeps the old behaviour: the
        // map is sized from InitialWorld, or from the first full snapshot.
        if (joinAccepted.World is { } worldWire)
        {
            ApplyWorldDescriptor(runtime, worldWire, joinAccepted.InitialWorld);
        }

        if (!joinAccepted.InitialWorld.HasValue && joinAccepted.EffectiveInitialActors.Count == 0)
        {
            return;
        }

        if (joinAccepted.InitialWorld.HasValue && joinAccepted.InitialWorldMutationSequence.HasValue)
        {
            // Deltas up to this sequence are already part of InitialWorld; replaying them duplicates cubes.
            runtime.AcknowledgeReplicatedWorldMutations(joinAccepted.InitialWorldMutationSequence.Value);
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

    private static void ApplyWorldDescriptor(IClientWorldState runtime, WorldDescriptorWire wire, WorldSnapshot? initialWorld)
    {
        WorldDescriptor descriptor;
        try
        {
            descriptor = wire.ToDescriptor();
            if (descriptor.MapSource == WorldMapSource.Stored)
            {
                // Replaced by the terrain-transfer build in wave E (B).
                throw new UnsupportedWorldException("stored worlds need a v8 terrain transfer");
            }

            if (descriptor.MapSource == WorldMapSource.Generated &&
                !WorldGeneratorCatalog.IsSupported(descriptor.GeneratorKey, descriptor.GeneratorVersion))
            {
                throw new UnsupportedWorldException(
                    $"The server's world \"{descriptor.Name}\" uses generator \"{descriptor.GeneratorKey}\" " +
                    $"version {descriptor.GeneratorVersion}, which this client does not support.");
            }

            descriptor.Validate();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
        {
            throw new UnsupportedWorldException($"The server's world is not supported by this client: {ex.Message}", ex);
        }

        if (initialWorld is { } world && (world.Width != descriptor.Width || world.Depth != descriptor.Depth))
        {
            throw new InvalidDataException(
                $"Protocol error: the join-accepted world is {descriptor.Width}x{descriptor.Depth} " +
                $"but its initial world is {world.Width}x{world.Depth}.");
        }

        if (runtime.Map.Width != descriptor.Width || runtime.Map.Depth != descriptor.Depth)
        {
            runtime.RebuildMapForWorld(descriptor);
        }
        else
        {
            runtime.AdoptWorldDescriptor(descriptor);
        }
    }

    public static void ApplyToPresenter(ClientScenePresenter presenter, JoinAcceptedMessage joinAccepted)
    {
        ArgumentNullException.ThrowIfNull(presenter);
        presenter.ApplyJoinAccepted(joinAccepted);
    }
}

// The server's world cannot be hosted by this client (an unknown generator or version, an unsupported
// map source or an out-of-range size). The session ends with this message as its reason.
public sealed class UnsupportedWorldException : Exception
{
    public UnsupportedWorldException(string message) : base(message)
    {
    }

    public UnsupportedWorldException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
