using System.Diagnostics;
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
using Dream.Simulation.World.MapSources;
using Dream.Simulation.World.Terrain;
using Dream.Tests.Server;

namespace Dream.Tests.Client;

// Persistence wave E, fix loop 1: hostile or damaged stored terrain on the client, and a stored-world
// resync it cannot host.
public sealed class StoredTerrainFixLoopTests
{
    private static readonly MapDefinition StoredDefinition =
        new(56, 8, 56, WorldMapSource.Generated, WorldGeneratorCatalog.DefaultKey, WorldGeneratorCatalog.DefaultVersion);

    // Chunk (0, 0) of a 56x56x8 world with every one of its 2048 cells at resolution 40: a few KB on the
    // wire, 131 million implied subcells. shape SolidCube cells carry an all-empty subdivision; others none.
    private static PersistedTerrainChunk HostileChunk(GeometryShapeKind shape, bool subdivided)
    {
        List<GeometryCellRecord> cells = [];
        for (int x = 0; x < 16; x++)
        {
            for (int z = 0; z < 16; z++)
            {
                for (int y = 0; y < 8; y++)
                {
                    GeometrySubdivisionState? state = null;
                    if (subdivided)
                    {
                        state = new GeometrySubdivisionState(40);
                        state.SetAllChildrenEmpty();
                    }

                    cells.Add(new GeometryCellRecord(
                        new GeometryCellKey(x, y, z),
                        new GeometryCellDefinition(shape, GeometryMaterialId.Grass, 40, true, IsFloor: y == 0),
                        state));
                }
            }
        }

        byte[] data = TerrainChunkCodec.Encode(StoredDefinition, 0, 0, cells);
        return new PersistedTerrainChunk(0, 0, TerrainChunkFormat.CurrentVersion, cells.Count, data);
    }

    private static IReadOnlyList<PersistedTerrainChunk> WithChunk0(IReadOnlyList<PersistedTerrainChunk> good, PersistedTerrainChunk chunk0)
    {
        PersistedTerrainChunk[] chunks = [.. good];
        chunks[0] = chunk0;
        return chunks;
    }

    [Theory]
    [InlineData(GeometryShapeKind.SolidCube, true)]
    [InlineData(GeometryShapeKind.Slope, false)]
    public void AHostileBlobOfImpliedSubcells_IsAProtocolError_QuicklyAndWithBoundedAllocation(GeometryShapeKind shape, bool subdivided)
    {
        using StoredWorldFixture fixture = new();
        using RuntimeAuthoritativeSessionHost host = ServerBootstrap.CreateRuntimeHost(fixture.OpenBundle(), null);
        JoinAcceptedMessage join = host.JoinSession("a", "Alpha") with { ReceivedTerrain = host.JoinSession("a", "Alpha").TerrainPayload!.Chunks };
        PersistedTerrainChunk hostile = HostileChunk(shape, subdivided);
        Assert.True(hostile.Data.Length < 64 * 1024, $"the hostile blob is {hostile.Data.Length} bytes");
        IReadOnlyList<PersistedTerrainChunk> chunks = WithChunk0(join.ReceivedTerrain!, hostile);

        // The digest of the hostile terrain is valid: the server side never decodes, so it would announce it.
        WorldDescriptor world = host.Runtime.World;
        Assert.NotNull(TerrainTransferPayload.Create(world, chunks).Manifest.Sha256);

        using ClientWorldSession client = new(20, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => ClientJoinAcceptedApplier.ApplyToRuntime(client, join with { ReceivedTerrain = chunks }));

        Assert.StartsWith("Protocol error: stored terrain", error.Message, StringComparison.Ordinal);
        Assert.Contains("implied subcell budget", error.Message, StringComparison.Ordinal);
        // "Quickly" is proved by the allocation bound, not by a clock. The decoder gives up as
        // soon as the implied-subcell budget is charged, so it never materialises the blob: that
        // is what the bound below measures, and it is the same number on an idle and on a
        // saturated host. The old clock budget measured the machine instead, and a host busy
        // enough to breach it says nothing about whether the decoder gave up early.
        Assert.True(
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore < 128L * 1024 * 1024,
            $"the refusal allocated {(GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / (1024 * 1024)} MB");
        Assert.Equal(20, client.Map.Width);
    }

    [Fact]
    public void TheDecoder_ChargesImpliedSubcellsBeforeAddingTheRecord()
    {
        PersistedTerrainChunk hostile = HostileChunk(GeometryShapeKind.Slope, subdivided: false);
        long overrides = long.MaxValue;
        long implied = 3 * 64_000L + 1;

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => TerrainChunkCodec.Decode(StoredDefinition, 0, 0, hostile.Data, ref overrides, ref implied));

        Assert.Contains("implied subcell budget", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, implied);
    }

    [Fact]
    public void ACorruptBodyWithValidStructure_LeavesTheExistingMapAndItsPhysicsIntact()
    {
        using StoredWorldFixture fixture = new();
        using RuntimeAuthoritativeSessionHost host = ServerBootstrap.CreateRuntimeHost(fixture.OpenBundle(), null);
        JoinAcceptedMessage good = host.JoinSession("a", "Alpha");
        JoinAcceptedMessage join = good with { ReceivedTerrain = good.TerrainPayload!.Chunks };
        byte[] corrupt = (byte[])join.ReceivedTerrain![0].Data.Clone();
        const int headerSize = 23;
        corrupt[headerSize + (4 * corrupt[headerSize - 1]) + 5] = 0;
        IReadOnlyList<PersistedTerrainChunk> chunks = WithChunk0(join.ReceivedTerrain!, join.ReceivedTerrain![0] with { Data = corrupt });
        using ClientWorldSession client = new(20, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        Map before = client.Map;
        int bodiesBefore = client.Physics.MapCubeBodies.Count;
        Assert.True(bodiesBefore > 0);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => ClientJoinAcceptedApplier.ApplyToRuntime(client, join with { ReceivedTerrain = chunks }));

        Assert.StartsWith("Protocol error: stored terrain", error.Message, StringComparison.Ordinal);
        Assert.Same(before, client.Map);
        Assert.Equal(bodiesBefore, client.Physics.MapCubeBodies.Count);
        Assert.Equal(WorldMapSource.Generated, client.World.MapSource);
    }

    [Fact]
    public void AStoredResync_WithOtherDimensions_EndsTheSessionInsteadOfThrowing()
    {
        using StoredWorldFixture fixture = new();
        using RuntimeAuthoritativeSessionHost host = ServerBootstrap.CreateRuntimeHost(fixture.OpenBundle(), null);
        JoinAcceptedMessage join = host.JoinSession("a", "Alpha");
        using ClientWorldSession client = new(20, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        ClientJoinAcceptedApplier.ApplyToRuntime(client, join with { ReceivedTerrain = join.TerrainPayload!.Chunks });
        ServerSnapshotEnvelope snapshot = new(
            1,
            new Dictionary<string, ulong>(),
            Array.Empty<ActorSnapshot>(),
            client.InteractionMode,
            new WorldSnapshot(14, client.Map.Height, 14, Array.Empty<Vector3>()),
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());
        List<string> ended = [];

        bool applied = RemoteWorldMutationApplier.TryApplySnapshotToRuntime(client, snapshot, ended.Add);

        Assert.False(applied);
        string reason = Assert.Single(ended);
        Assert.StartsWith("Protocol error:", reason, StringComparison.Ordinal);
        Assert.Equal(56, client.Map.Width);
    }

    [Fact]
    public void ANonSquareWorldSnapshot_EndsTheSessionAsAProtocolError_NotAnInvalidOperation()
    {
        using ClientWorldSession client = new(20, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        ServerSnapshotEnvelope snapshot = new(
            1,
            new Dictionary<string, ulong>(),
            Array.Empty<ActorSnapshot>(),
            client.InteractionMode,
            new WorldSnapshot(14, client.Map.Height, 20, Array.Empty<Vector3>()),
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());
        List<string> ended = [];

        bool applied = RemoteWorldMutationApplier.TryApplySnapshotToRuntime(client, snapshot, ended.Add);

        Assert.False(applied);
        Assert.StartsWith("Protocol error:", Assert.Single(ended), StringComparison.Ordinal);
    }

    [Fact]
    public void AGeneratedJoin_ReplacesAStoredMap_EvenAtTheSameSize()
    {
        using StoredWorldFixture fixture = new();
        using RuntimeAuthoritativeSessionHost stored = ServerBootstrap.CreateRuntimeHost(fixture.OpenBundle(), null);
        JoinAcceptedMessage storedJoin = stored.JoinSession("a", "Alpha");
        using ClientWorldSession client = new(20, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        ClientJoinAcceptedApplier.ApplyToRuntime(client, storedJoin with { ReceivedTerrain = storedJoin.TerrainPayload!.Chunks });
        Map storedMap = client.Map;
        using RuntimeAuthoritativeSessionHost generated = ServerBootstrap.CreateRuntimeHost(null, null);

        ClientJoinAcceptedApplier.ApplyToRuntime(client, generated.JoinSession("b", "Beta"));

        Assert.NotSame(storedMap, client.Map);
        Assert.Equal(WorldMapSource.Generated, client.World.MapSource);
        Assert.Equal(56, client.Map.Width);
    }

    [Fact]
    public void ASessionsConfiguredLimits_BoundTheDecode()
    {
        using StoredWorldFixture fixture = new();
        using RuntimeAuthoritativeSessionHost host = ServerBootstrap.CreateRuntimeHost(fixture.OpenBundle(), null);
        JoinAcceptedMessage join = host.JoinSession("a", "Alpha");
        join = join with { ReceivedTerrain = join.TerrainPayload!.Chunks };
        using ClientWorldSession client = new(20, new Vector3(5f, 1.5f, 5f), new SceneEventHub());

        // Floors are coarse SolidCubes (free); the generated sculptures are not, so a tiny cap trips.
        WorldTransferLimits tight = WorldTransferLimits.Default with { MaxTerrainSubcells = 1 };

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => ClientJoinAcceptedApplier.ApplyToRuntime(client, join, tight));
        Assert.Contains("implied subcell budget", error.Message, StringComparison.Ordinal);
        ClientJoinAcceptedApplier.ApplyToRuntime(client, join);
        Assert.Equal(WorldMapSource.Stored, client.World.MapSource);
    }
}
