using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Replication;
using Dream.Client.World;
using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Simulation.Persistence;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Server;

namespace Dream.Tests.Client;

// Persistence wave E (B): a client builds a stored world's map from the received terrain.
public sealed class StoredWorldJoinApplierTests
{
    [Fact]
    public void AStoredJoin_BuildsAMapEqualToTheServers()
    {
        using StoredWorldFixture fixture = new();
        using RuntimeAuthoritativeSessionHost host = ServerBootstrap.CreateRuntimeHost(fixture.OpenBundle(), null);
        JoinAcceptedMessage join = Received(host.JoinSession("a", "Alpha"));
        using ClientWorldSession client = CreateClient(20);

        ClientJoinAcceptedApplier.ApplyToRuntime(client, join);

        Assert.Equal(host.Runtime.Map.WorldChecksum, client.Map.WorldChecksum);
        Assert.Equal(
            host.Runtime.Map.GeometryCells.Keys.OrderBy(key => key.ToString(), StringComparer.Ordinal),
            client.Map.GeometryCells.Keys.OrderBy(key => key.ToString(), StringComparer.Ordinal));
        Assert.Equal(host.Runtime.Physics.MapCubeBodies.Count, client.Physics.MapCubeBodies.Count);
        Assert.Equal(WorldMapSource.Stored, client.World.MapSource);
        Assert.Equal(56, client.Map.Width);
    }

    [Fact]
    public void AStoredRebuild_DoesNotReplayTheOldMapsRemovals()
    {
        using StoredWorldFixture fixture = new();
        using RuntimeAuthoritativeSessionHost host = ServerBootstrap.CreateRuntimeHost(fixture.OpenBundle(), null);
        JoinAcceptedMessage join = Received(host.JoinSession("a", "Alpha"));
        using ClientWorldSession client = CreateClient(56);
        GeometryCellKey removed = client.Map.GeometryCells.Keys.First(key => key.Y > 0);
        Assert.True(client.Map.RemoveSourceCell(removed));
        Assert.False(client.Map.GeometryCells.ContainsKey(removed));

        ClientJoinAcceptedApplier.ApplyToRuntime(client, join);

        Assert.True(client.Map.GeometryCells.ContainsKey(removed));
        Assert.Equal(host.Runtime.Map.WorldChecksum, client.Map.WorldChecksum);
    }

    [Fact]
    public void AStoredWorldWithoutTerrain_IsRejected()
    {
        using StoredWorldFixture fixture = new();
        using RuntimeAuthoritativeSessionHost host = ServerBootstrap.CreateRuntimeHost(fixture.OpenBundle(), null);
        JoinAcceptedMessage join = host.JoinSession("a", "Alpha");
        using ClientWorldSession client = CreateClient(20);

        UnsupportedWorldException error = Assert.Throws<UnsupportedWorldException>(
            () => ClientJoinAcceptedApplier.ApplyToRuntime(client, join with { ReceivedTerrain = null }));

        Assert.Contains("no terrain", error.Message, StringComparison.Ordinal);
        Assert.Equal(20, client.Map.Width);
    }

    [Fact]
    public void AChunkSetThatDoesNotCoverTheWorld_EndsTheJoinAsAProtocolError()
    {
        using StoredWorldFixture fixture = new();
        using RuntimeAuthoritativeSessionHost host = ServerBootstrap.CreateRuntimeHost(fixture.OpenBundle(), null);
        JoinAcceptedMessage join = Received(host.JoinSession("a", "Alpha"));
        using ClientWorldSession client = CreateClient(20);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => ClientJoinAcceptedApplier.ApplyToRuntime(client, join with { ReceivedTerrain = join.ReceivedTerrain!.Skip(1).ToArray() }));

        Assert.StartsWith("Protocol error: stored terrain", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnsupportedChunkVersion_EndsTheJoinAsAProtocolError()
    {
        using StoredWorldFixture fixture = new();
        using RuntimeAuthoritativeSessionHost host = ServerBootstrap.CreateRuntimeHost(fixture.OpenBundle(), null);
        JoinAcceptedMessage join = Received(host.JoinSession("a", "Alpha"));
        PersistedTerrainChunk[] chunks = [.. join.ReceivedTerrain!];
        chunks[0] = chunks[0] with { FormatVersion = 99 };
        using ClientWorldSession client = CreateClient(20);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => ClientJoinAcceptedApplier.ApplyToRuntime(client, join with { ReceivedTerrain = chunks }));

        Assert.StartsWith("Protocol error: stored terrain", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AResync_WithOtherDimensions_OnAStoredWorld_Throws()
    {
        using StoredWorldFixture fixture = new();
        using RuntimeAuthoritativeSessionHost host = ServerBootstrap.CreateRuntimeHost(fixture.OpenBundle(), null);
        using ClientWorldSession client = CreateClient(20);
        ClientJoinAcceptedApplier.ApplyToRuntime(client, Received(host.JoinSession("a", "Alpha")));
        ServerSnapshotEnvelope snapshot = new(
            1,
            new Dictionary<string, ulong>(),
            Array.Empty<ActorSnapshot>(),
            client.InteractionMode,
            new WorldSnapshot(14, client.Map.Height, 14, Array.Empty<Vector3>()),
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());

        Assert.Throws<InvalidDataException>(() => RemoteWorldMutationApplier.ApplySnapshotToRuntime(client, snapshot));
        Assert.Equal(56, client.Map.Width);
    }

    [Fact]
    public void AGeneratedJoin_BehavesAsBefore()
    {
        using RuntimeAuthoritativeSessionHost host = ServerBootstrap.CreateRuntimeHost(null, null);
        JoinAcceptedMessage join = host.JoinSession("a", "Alpha");
        using ClientWorldSession client = CreateClient(20);

        ClientJoinAcceptedApplier.ApplyToRuntime(client, join);

        Assert.Equal(56, client.Map.Width);
        Assert.Equal(WorldMapSource.Generated, client.World.MapSource);
    }

    private static JoinAcceptedMessage Received(JoinAcceptedMessage join) =>
        join with { ReceivedTerrain = join.TerrainPayload!.Chunks };

    private static ClientWorldSession CreateClient(int floorSize) =>
        new(floorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
}
