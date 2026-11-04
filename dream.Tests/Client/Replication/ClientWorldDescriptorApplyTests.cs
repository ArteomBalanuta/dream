using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Replication;
using Dream.Client.World;
using Dream.Multiplayer.Protocol;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Client.Replication;

// Persistence wave B3: the client builds its map from the join-accepted world descriptor, before
// any snapshot, and ends the session on a world it cannot host.
public class ClientWorldDescriptorApplyTests
{
    [Fact]
    public void FloorSize20Client_JoiningA56World_GetsA56Map_BeforeTheFirstSnapshot()
    {
        using ClientWorldSession client = CreateClient(20);
        Assert.Equal(20, client.Map.Width);
        JoinAcceptedMessage join = Join(WorldDescriptor.Default, initialWorld: null);

        ClientJoinAcceptedApplier.ApplyToRuntime(client, join);

        Assert.Equal(56, client.Map.Width);
        Assert.Equal(56, client.Map.Depth);
        Assert.Equal(WorldDescriptor.Default, client.World);
    }

    [Fact]
    public void ADescriptorOfADifferentSize_RebuildsTheMap_EvenWithNoInitialActors()
    {
        using ClientWorldSession client = CreateClient(56);
        WorldDescriptor small = WorldDescriptor.ForFloorSize(20, new WorldId(2), "small");

        ClientJoinAcceptedApplier.ApplyToRuntime(client, Join(small, initialWorld: null));

        Assert.Equal(20, client.Map.Width);
        Assert.Equal(small, client.World);
    }

    [Fact]
    public void ADescriptorOfTheSameSize_KeepsTheMap_AndAdoptsTheDescriptor()
    {
        using ClientWorldSession client = CreateClient(20);
        Map before = client.Map;
        WorldDescriptor named = WorldDescriptor.ForFloorSize(20, new WorldId(3), "same");

        ClientJoinAcceptedApplier.ApplyToRuntime(client, Join(named, initialWorld: null));

        Assert.Same(before, client.Map);
        Assert.Equal(named, client.World);
    }

    [Fact]
    public void ADescriptorOfTheSameSizeWithAnotherGenerator_RebuildsTheMap()
    {
        // Size alone is not enough to decide the map can stand: the generator key and version
        // decide the material of every cell, and the floor is outside WorldChecksum
        // (WorldStateChecksum skips Key.Y <= 0), so adopting the descriptor and keeping the old
        // map would silently show the wrong ground.
        using ClientWorldSession client = CreateClient(20);
        Map before = client.Map;
        Assert.Equal(WorldGeneratorCatalog.DefaultKey, client.World.GeneratorKey);

        WorldDescriptor natural = WorldDescriptor.ForFloorSize(
            20,
            new WorldId(4),
            "wilds",
            WorldGeneratorCatalog.NaturalKey);

        ClientJoinAcceptedApplier.ApplyToRuntime(client, Join(natural, initialWorld: null));

        Assert.NotSame(before, client.Map);
        Assert.Equal(natural, client.World);
        Assert.Equal(20, client.Map.Width);

        // The rebuilt map is the natural generator's ground, not the default one's.
        IReadOnlyList<Dream.Simulation.World.Geometry.GeometryCellRecord> expected =
            Dream.Simulation.World.MapSources.MapSources.ForWorld(natural, seed: 0).CreateCells();
        foreach (Dream.Simulation.World.Geometry.GeometryCellRecord cell in expected)
        {
            Assert.Equal(
                cell.Definition.MaterialId,
                client.Map.GeometryCells[cell.Key].Definition.MaterialId);
        }
    }

    [Fact]
    public void WithoutADescriptor_TheMapIsStillSizedFromTheInitialWorld()
    {
        using ClientWorldSession client = CreateClient(20);
        JoinAcceptedMessage join = new(
            "session-a", 101, 0, new WorldSnapshot(10, client.Map.Height, 10, Array.Empty<Vector3>()));

        ClientJoinAcceptedApplier.ApplyToRuntime(client, join);

        Assert.Equal(10, client.Map.Width);
    }

    [Fact]
    public void AMatchingInitialWorld_IsAccepted()
    {
        using ClientWorldSession client = CreateClient(20);
        WorldDescriptor small = WorldDescriptor.ForFloorSize(12, new WorldId(2), "small");
        WorldSnapshot world = new(12, small.Height, 12, Array.Empty<Vector3>());

        ClientJoinAcceptedApplier.ApplyToRuntime(client, Join(small, world));

        Assert.Equal(12, client.Map.Width);
    }

    [Fact]
    public void AnInitialWorldOfAnotherSize_IsAProtocolError()
    {
        using ClientWorldSession client = CreateClient(20);
        WorldSnapshot world = new(10, 8, 10, Array.Empty<Vector3>());

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            ClientJoinAcceptedApplier.ApplyToRuntime(client, Join(WorldDescriptor.ForFloorSize(12), world)));

        Assert.Contains("12x12", error.Message, StringComparison.Ordinal);
        Assert.Contains("10x10", error.Message, StringComparison.Ordinal);
        Assert.Equal(20, client.Map.Width);
    }

    [Theory]
    [InlineData("future", 1)]
    [InlineData("default", 2)]
    public void AnUnsupportedGenerator_ThrowsAReadableException_AndLeavesTheMapAlone(string key, int version)
    {
        using ClientWorldSession client = CreateClient(20);
        WorldDescriptorWire wire = WorldDescriptorWire.From(WorldDescriptor.ForFloorSize(30)) with
        {
            GeneratorKey = key,
            GeneratorVersion = version
        };
        JoinAcceptedMessage join = new("session-a", 101, 0, InitialWorld: null, World: wire);

        UnsupportedWorldException error = Assert.Throws<UnsupportedWorldException>(() =>
            ClientJoinAcceptedApplier.ApplyToRuntime(client, join));

        Assert.Contains(key, error.Message, StringComparison.Ordinal);
        Assert.Contains("does not support", error.Message, StringComparison.Ordinal);
        Assert.Equal(20, client.Map.Width);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(101)]
    public void AnOutOfRangeSize_IsUnsupported(int width)
    {
        using ClientWorldSession client = CreateClient(20);
        WorldDescriptorWire wire = WorldDescriptorWire.From(WorldDescriptor.ForFloorSize(30)) with { Width = width, Depth = width };

        Assert.Throws<UnsupportedWorldException>(() => ClientJoinAcceptedApplier.ApplyToRuntime(
            client, new JoinAcceptedMessage("session-a", 101, 0, InitialWorld: null, World: wire)));
    }

    [Fact]
    public void AStoredMapSource_WithoutTerrain_IsUnsupported()
    {
        using ClientWorldSession client = CreateClient(20);
        WorldDescriptorWire wire = WorldDescriptorWire.From(WorldDescriptor.Default) with { MapSource = "stored" };

        UnsupportedWorldException error = Assert.Throws<UnsupportedWorldException>(() => ClientJoinAcceptedApplier.ApplyToRuntime(
            client, new JoinAcceptedMessage("session-a", 101, 0, InitialWorld: null, World: wire)));

        Assert.Equal("the server sent no terrain for its stored world", error.Message);
        Assert.Equal(20, client.Map.Width);
    }

    [Fact]
    public void AStoredWorld_WithEmptyReceivedTerrain_AndAnUnknownProvenanceGenerator_IsAProtocolError()
    {
        using ClientWorldSession client = CreateClient(20);
        WorldDescriptorWire wire = WorldDescriptorWire.From(WorldDescriptor.Default) with { MapSource = "stored", GeneratorKey = "someday" };
        JoinAcceptedMessage join = new JoinAcceptedMessage("session-a", 101, 0, InitialWorld: null, World: wire) with
        {
            ReceivedTerrain = Array.Empty<Dream.Simulation.Persistence.PersistedTerrainChunk>()
        };

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => ClientJoinAcceptedApplier.ApplyToRuntime(client, join));

        Assert.StartsWith("Protocol error: stored terrain", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RebuildingFromASource_BuildsTheMapFromIt()
    {
        using ClientWorldSession client = CreateClient(20);
        WorldDescriptor world = WorldDescriptor.ForFloorSize(30);

        client.RebuildMapForWorld(
            world,
            new Dream.Simulation.World.MapSources.GeneratedMapSource(
                Dream.Simulation.World.MapSources.MapDefinition.ForWorld(world)));

        Assert.Equal(30, client.Map.Width);
        Assert.Equal(30, client.World.Width);
    }

    [Fact]
    public void AFullSnapshotResync_OfAnotherSize_GoesThroughTheCurrentDescriptor()
    {
        using ClientWorldSession client = CreateClient(20);
        ClientJoinAcceptedApplier.ApplyToRuntime(client, Join(WorldDescriptor.ForFloorSize(20, new WorldId(5), "named"), null));
        ServerSnapshotEnvelope snapshot = new(
            1,
            new Dictionary<string, ulong>(),
            Array.Empty<ActorSnapshot>(),
            client.InteractionMode,
            new WorldSnapshot(14, client.Map.Height, 14, Array.Empty<Vector3>()),
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());

        RemoteWorldMutationApplier.ApplySnapshotToRuntime(client, snapshot);

        Assert.Equal(14, client.Map.Width);
        Assert.Equal(14, client.World.Width);
        Assert.Equal(14, client.World.Depth);
        Assert.Equal(new WorldId(5), client.World.Id);
        Assert.Equal("named", client.World.Name);
    }

    private static JoinAcceptedMessage Join(WorldDescriptor world, WorldSnapshot? initialWorld) =>
        new("session-a", 101, 0, initialWorld, World: WorldDescriptorWire.From(world));

    private static ClientWorldSession CreateClient(int floorSize) =>
        new(floorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
}
