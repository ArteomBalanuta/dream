using Dream.Client.Presentation;
using Dream.Multiplayer.Protocol;
using Dream.Simulation.World.MapSources;
using Dream.Simulation.World.Terrain;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Client.Replication;

public static class ClientJoinAcceptedApplier
{
    // limits: the bounds the session received the join under; null uses WorldTransferLimits.Default.
    public static void ApplyToRuntime(
        IClientWorldState runtime, JoinAcceptedMessage joinAccepted, WorldTransferLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        // The descriptor comes first, so the map has the server's size before any cube or snapshot
        // reaches it. A null World (a server that does not send one) keeps the old behaviour: the
        // map is sized from InitialWorld, or from the first full snapshot.
        if (joinAccepted.World is { } worldWire)
        {
            ApplyWorldDescriptor(runtime, worldWire, joinAccepted.InitialWorld, joinAccepted, limits ?? WorldTransferLimits.Default);
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

    private static void ApplyWorldDescriptor(IClientWorldState runtime, WorldDescriptorWire wire, WorldSnapshot? initialWorld,
        JoinAcceptedMessage joinAccepted, WorldTransferLimits limits)
    {
        WorldDescriptor descriptor;
        try
        {
            descriptor = wire.ToDescriptor();
            if (descriptor.MapSource == WorldMapSource.Stored)
            {
                if (joinAccepted.ReceivedTerrain == null)
                {
                    throw new UnsupportedWorldException("the server sent no terrain for its stored world");
                }
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

        if (descriptor.MapSource == WorldMapSource.Stored)
        {
            StoredMapSource source;
            try
            {
                source = new StoredMapSource(
                    MapDefinition.ForWorld(descriptor),
                    joinAccepted.ReceivedTerrain!,
                    limits.MaxTerrainSubcellOverrides,
                    limits.MaxTerrainSubcells);
                // Decode (and so validate and budget-check) before the runtime is touched: a corrupt or
                // hostile body must leave the existing map and physics bodies intact. Map decodes again.
                _ = source.CreateCells();
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidDataException($"Protocol error: stored terrain {ex.Message}", ex);
            }

            runtime.RebuildMapForWorld(descriptor, source);
            return;
        }

        // A stored map never stands in for a generated world, even at the same size.
        if (runtime.Map.Width != descriptor.Width || runtime.Map.Depth != descriptor.Depth ||
            runtime.World.MapSource == WorldMapSource.Stored)
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
