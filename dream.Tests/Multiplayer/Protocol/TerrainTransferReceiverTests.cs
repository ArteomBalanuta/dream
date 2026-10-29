using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Dream.Simulation.World.Terrain;
using static Dream.Tests.Multiplayer.Protocol.TerrainTransferTestData;

namespace Dream.Tests.Multiplayer.Protocol;

// Protocol v8: the client end of terrain transfer. One test per validation rule of the wave E plan;
// every failure is a protocol error (the session ends), none is silently skipped.
public sealed class TerrainTransferReceiverTests
{
    private const int Id = 5;

    private static readonly TerrainTransferPayload Terrain = Payload();

    // ---- Happy path ---------------------------------------------------------------------------

    [Fact]
    public void Terrain_ThenWorld_DeliversTheJoinWithReceivedTerrain_AndHeldMessagesAfterIt()
    {
        Assert.True(Terrain.Fragments.Count >= 3, "the fixture should span several fragments");
        JoinAcceptedMessage carrier = Carrier(Terrain, Id);
        (WorldTransferReassembler reassembler, List<IServerToClientMessage> delivered) = Started(carrier);
        ServerHelloMessage held = new("held", 8, 1);

        foreach (TerrainChunkMessage fragment in Fragments(Terrain, Id))
        {
            Assert.Null(reassembler.Accept(fragment, delivered.Add));
            reassembler.Accept(held, delivered.Add);
        }

        Assert.Empty(delivered);
        WorldTransferReceiver? completed = reassembler.Accept(FinalWorldChunk(Id), delivered.Add);

        Assert.NotNull(completed);
        JoinAcceptedMessage join = Assert.IsType<JoinAcceptedMessage>(delivered[0]);
        Assert.NotNull(join.InitialWorld);
        Assert.Null(join.WorldTransferId);
        Assert.Equal(Terrain.Manifest, join.Terrain);
        Assert.NotNull(join.ReceivedTerrain);
        Assert.Equal(Terrain.Chunks.Count, join.ReceivedTerrain.Count);
        for (int i = 0; i < Terrain.Chunks.Count; i++)
        {
            Assert.Equal(Terrain.Chunks[i], join.ReceivedTerrain[i]);
            Assert.Equal(Terrain.Chunks[i].Data, join.ReceivedTerrain[i].Data);
        }

        Assert.Equal(Terrain.Fragments.Count, delivered.Count - 1);
        Assert.All(delivered.Skip(1), message => Assert.Same(held, message));
        Assert.Null(reassembler.Current);
    }

    [Fact]
    public void GeneratedCarrier_WithoutTerrain_DeliversNoReceivedTerrain()
    {
        JoinAcceptedMessage carrier = Carrier(null, Id);
        (WorldTransferReassembler reassembler, List<IServerToClientMessage> delivered) = Started(carrier);

        reassembler.Accept(FinalWorldChunk(Id), delivered.Add);

        Assert.Null(Assert.IsType<JoinAcceptedMessage>(Assert.Single(delivered)).ReceivedTerrain);
    }

    // ---- The carrier's manifest -----------------------------------------------------------------

    [Fact]
    public void Carrier_WithTerrain_AndNoWorld_Fails()
    {
        JoinAcceptedMessage carrier = Carrier(Terrain, Id) with { World = null };

        Assert.Throws<InvalidDataException>(() => Started(carrier));
    }

    [Fact]
    public void Carrier_WithTerrain_ForAGeneratedWorld_Fails()
    {
        JoinAcceptedMessage carrier = Carrier(Terrain, Id);
        carrier = carrier with { World = carrier.World! with { MapSource = "generated" } };

        Assert.Throws<InvalidDataException>(() => Started(carrier));
    }

    [Fact]
    public void Carrier_ChunkCountOtherThanTheWorldsGrid_Fails()
    {
        Assert.Throws<InvalidDataException>(() => Started(WithManifest(m => m with { Chunks = 8 })));
        Assert.Throws<InvalidDataException>(() => Started(WithManifest(m => m with { Chunks = 10 })));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10)]
    public void Carrier_FragmentsOutsideOneToChunks_Fails(int fragments)
    {
        Assert.Throws<InvalidDataException>(() => Started(WithManifest(m => m with { Fragments = fragments })));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    public void Carrier_NonPositiveBytes_Fails(long bytes)
    {
        Assert.Throws<InvalidDataException>(() => Started(WithManifest(m => m with { Bytes = bytes })));
    }

    [Fact]
    public void Carrier_BytesOverTheLimit_IsALimitError()
    {
        WorldTransferLimits limits = new() { MaxTerrainBytes = Terrain.Manifest.Bytes - 1 };

        Assert.Throws<WorldTransferLimitException>(() => Started(Carrier(Terrain, Id), limits));
        _ = Started(Carrier(Terrain, Id), new WorldTransferLimits { MaxTerrainBytes = Terrain.Manifest.Bytes });
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")] // uppercase
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg")] // not hex
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")] // 65 characters
    public void Carrier_Sha256NotSixtyFourLowercaseHex_Fails(string sha)
    {
        Assert.Throws<InvalidDataException>(() => Started(WithManifest(m => m with { Sha256 = sha })));
    }

    // ---- Terrain frames in the wrong place ------------------------------------------------------

    [Fact]
    public void TerrainFrame_WithNoTransferInProgress_Fails()
    {
        WorldTransferReassembler reassembler = new();

        Assert.Throws<InvalidOperationException>(() => reassembler.Accept(Fragments(Terrain, Id)[0], _ => { }));
    }

    [Fact]
    public void TerrainFrame_WhenTheCarrierHasNoManifest_Fails()
    {
        (WorldTransferReassembler reassembler, _) = Started(Carrier(null, Id));

        Assert.Throws<InvalidOperationException>(() => reassembler.Accept(Fragments(Terrain, Id)[0], _ => { }));
    }

    [Fact]
    public void TerrainFrame_ForASnapshotCarrier_Fails()
    {
        WorldTransferReassembler reassembler = new();
        reassembler.Accept(
            new ServerSnapshotMessage(
                new Dream.Simulation.World.ServerSnapshotEnvelope(1, new Dictionary<string, ulong>(), [], Dream.Simulation.Players.PlayerInteractionMode.Building, null, [], []),
                Id),
            _ => { });

        Assert.Throws<InvalidOperationException>(() => reassembler.Accept(Fragments(Terrain, Id)[0], _ => { }));
    }

    [Fact]
    public void TerrainFrame_WithTheWrongTransferId_Fails()
    {
        (WorldTransferReassembler reassembler, _) = Started(Carrier(Terrain, Id));

        Assert.Throws<InvalidOperationException>(() => reassembler.Accept(Fragments(Terrain, Id + 1)[0], _ => { }));
    }

    [Fact]
    public void TerrainFrame_OutOfOrder_Fails()
    {
        (WorldTransferReassembler reassembler, _) = Started(Carrier(Terrain, Id));
        List<TerrainChunkMessage> fragments = Fragments(Terrain, Id);

        Assert.Throws<InvalidOperationException>(() => reassembler.Accept(fragments[1], _ => { }));
    }

    [Fact]
    public void TerrainFrame_Repeated_Fails()
    {
        (WorldTransferReassembler reassembler, _) = Started(Carrier(Terrain, Id));
        List<TerrainChunkMessage> fragments = Fragments(Terrain, Id);
        reassembler.Accept(fragments[0], _ => { });

        Assert.Throws<InvalidOperationException>(() => reassembler.Accept(fragments[0], _ => { }));
    }

    [Fact]
    public void TerrainFrame_AfterAWorldChunk_Fails()
    {
        (WorldTransferReassembler reassembler, _) = Started(Carrier(Terrain, Id));
        FeedTerrain(reassembler);
        reassembler.Accept(FirstOfTwoWorldChunks(Id), _ => { });

        Assert.Throws<InvalidOperationException>(() => reassembler.Accept(Fragments(Terrain, Id)[0], _ => { }));
    }

    [Fact]
    public void TerrainFrame_AfterTheFinalFragment_Fails()
    {
        (WorldTransferReassembler reassembler, _) = Started(Carrier(Terrain, Id));
        FeedTerrain(reassembler);
        TerrainChunkMessage extra = Fragments(Terrain, Id)[0] with { Index = Terrain.Fragments.Count, IsFinal = false };

        Assert.Throws<InvalidOperationException>(() => reassembler.Accept(extra, _ => { }));
    }

    [Fact]
    public void TerrainFrame_WithNoChunks_Fails()
    {
        (WorldTransferReassembler reassembler, _) = Started(Carrier(Terrain, Id));

        Assert.Throws<InvalidDataException>(() => reassembler.Accept(Fragments(Terrain, Id)[0] with { Chunks = [] }, _ => { }));
    }

    // ---- Per chunk entry ------------------------------------------------------------------------

    [Theory]
    [InlineData(3, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 3)]
    [InlineData(0, -1)]
    public void Chunk_OutsideTheGrid_Fails(int x, int z)
    {
        TerrainChunkMessage fragment = MutateFirstChunk(chunk => chunk with { ChunkX = x, ChunkZ = z });

        Assert.Throws<InvalidDataException>(() => AcceptFirst(fragment));
    }

    [Fact]
    public void Chunk_NotAfterThePreviousOne_InZxOrder_Fails()
    {
        (WorldTransferReassembler reassembler, _) = Started(Carrier(Terrain, Id));
        TerrainChunkMessage first = Fragments(Terrain, Id)[0];
        Assert.True(first.Chunks.Count >= 2);

        // A repeated entry, and a reversed pair.
        Assert.Throws<InvalidDataException>(() =>
            reassembler.Accept(first with { Chunks = [first.Chunks[0], first.Chunks[0]] }, _ => { }));

        (WorldTransferReassembler other, _) = Started(Carrier(Terrain, Id));
        Assert.Throws<InvalidDataException>(() =>
            other.Accept(first with { Chunks = [first.Chunks[1], first.Chunks[0]] }, _ => { }));
    }

    [Fact]
    public void Chunk_OrderIsZThenX_NotXThenZ()
    {
        // (x=2, z=0) then (x=0, z=1) is ascending in (z, x); (x=0, z=1) then (x=2, z=0) is not.
        (WorldTransferReassembler reassembler, _) = Started(Carrier(Terrain, Id));
        PersistedTerrainChunk a = Terrain.Chunks.Single(c => c is { ChunkX: 2, ChunkZ: 0 });
        PersistedTerrainChunk b = Terrain.Chunks.Single(c => c is { ChunkX: 0, ChunkZ: 1 });

        Assert.Throws<InvalidDataException>(() =>
            reassembler.Accept(Fragments(Terrain, Id)[0] with { Chunks = [b, a] }, _ => { }));
    }

    [Fact]
    public void Chunk_WithAnotherFormatVersion_Fails()
    {
        TerrainChunkMessage fragment = MutateFirstChunk(chunk => chunk with { FormatVersion = TerrainChunkFormat.CurrentVersion + 1 });

        Assert.Throws<InvalidDataException>(() => AcceptFirst(fragment));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData((16 * 16 * Height) + 1)]
    public void Chunk_CellCountOutsideZeroToChunkCapacity_Fails(int cells)
    {
        TerrainChunkMessage fragment = MutateFirstChunk(chunk => chunk with { CellCount = cells });

        Assert.Throws<InvalidDataException>(() => AcceptFirst(fragment));
    }

    [Fact]
    public void Chunk_CellCountAtCapacity_IsAccepted()
    {
        // Rejected only later, by the digest: the capacity bound itself admits 16 * 16 * H.
        TerrainChunkMessage fragment = MutateFirstChunk(chunk => chunk with { CellCount = 16 * 16 * Height });

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => FeedAll(Carrier(Terrain, Id), [fragment, .. Fragments(Terrain, Id).Skip(1)]));
        Assert.Contains("sha256", error.Message);
    }

    [Fact]
    public void Chunk_BlobOverTheLimit_IsALimitError()
    {
        WorldTransferLimits limits = new() { MaxTerrainBlobBytes = 1000 };
        (WorldTransferReassembler reassembler, _) = Started(Carrier(Terrain, Id), limits);

        Assert.Throws<WorldTransferLimitException>(() => reassembler.Accept(Fragments(Terrain, Id)[0], _ => { }));
    }

    [Fact]
    public void Chunk_WithAnEmptyBlob_Fails()
    {
        TerrainChunkMessage fragment = MutateFirstChunk(chunk => chunk with { Data = [] });

        Assert.Throws<InvalidDataException>(() => AcceptFirst(fragment));
    }

    [Fact]
    public void RunningBytes_PastTheManifest_Fail()
    {
        TerrainTransferManifest smaller = Terrain.Manifest with { Bytes = Terrain.Manifest.Bytes - 1 };
        JoinAcceptedMessage carrier = Carrier(Terrain, Id) with { Terrain = smaller };

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => FeedAll(carrier, [.. Fragments(Terrain, Id)]));
        Assert.Contains("bytes it announced", error.Message);
    }

    // ---- final, counts, digest ------------------------------------------------------------------

    [Fact]
    public void LastFragment_WithoutFinal_Fails()
    {
        List<TerrainChunkMessage> fragments = Fragments(Terrain, Id);
        fragments[^1] = fragments[^1] with { IsFinal = false };

        Assert.Throws<InvalidDataException>(() => FeedAll(Carrier(Terrain, Id), [.. fragments]));
    }

    [Fact]
    public void EarlierFragment_MarkedFinal_Fails()
    {
        List<TerrainChunkMessage> fragments = Fragments(Terrain, Id);
        fragments[0] = fragments[0] with { IsFinal = true };

        Assert.Throws<InvalidDataException>(() => FeedAll(Carrier(Terrain, Id), [.. fragments]));
    }

    [Fact]
    public void Final_WithFewerChunksThanAnnounced_Fails()
    {
        List<TerrainChunkMessage> fragments = Fragments(Terrain, Id);
        int multi = fragments.FindIndex(f => f.Chunks.Count >= 2);
        Assert.True(multi >= 0);
        fragments[multi] = fragments[multi] with { Chunks = fragments[multi].Chunks.Take(fragments[multi].Chunks.Count - 1).ToArray() };

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => FeedAll(Carrier(Terrain, Id), [.. fragments]));
        Assert.Contains("announced", error.Message);
    }

    [Fact]
    public void Final_WithADifferentDigest_Fails()
    {
        List<TerrainChunkMessage> fragments = Fragments(Terrain, Id);
        PersistedTerrainChunk victim = fragments[2].Chunks[0];
        byte[] data = (byte[])victim.Data.Clone();
        data[data.Length / 2] ^= 0x40;
        fragments[2] = fragments[2] with { Chunks = [WithData(victim, data), .. fragments[2].Chunks.Skip(1)] };

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => FeedAll(Carrier(Terrain, Id), [.. fragments]));
        Assert.Contains("sha256", error.Message);
    }

    [Fact]
    public void Final_WithTheManifestDigestOfOtherTerrain_Fails()
    {
        TerrainTransferPayload other = Payload(seed: 99);
        JoinAcceptedMessage carrier = Carrier(Terrain, Id) with { Terrain = Terrain.Manifest with { Sha256 = other.Manifest.Sha256 } };

        Assert.Throws<InvalidDataException>(() => FeedAll(carrier, [.. Fragments(Terrain, Id)]));
    }

    [Fact]
    public void WorldChunk_BeforeTheTerrainIsComplete_Fails()
    {
        (WorldTransferReassembler reassembler, _) = Started(Carrier(Terrain, Id));
        reassembler.Accept(Fragments(Terrain, Id)[0], _ => { });

        Assert.Throws<InvalidOperationException>(() => reassembler.Accept(FinalWorldChunk(Id), _ => { }));
    }

    [Fact]
    public void WorldChunk_BeforeAnyTerrain_Fails()
    {
        (WorldTransferReassembler reassembler, _) = Started(Carrier(Terrain, Id));

        Assert.Throws<InvalidOperationException>(() => reassembler.Accept(FinalWorldChunk(Id), _ => { }));
    }

    [Fact]
    public void CorrectTerrain_WithADifferentFragmentation_IsStillAccepted()
    {
        // Fragment boundaries are the server's choice; only the manifest's count must be met.
        TerrainTransferPayload coarse = Payload(fragmentRawBytes: 1_000_000);
        Assert.Equal(1, coarse.Fragments.Count);
        JoinAcceptedMessage carrier = Carrier(coarse, Id);

        List<IServerToClientMessage> delivered = FeedAll(carrier, [.. Fragments(coarse, Id), FinalWorldChunk(Id)]);

        Assert.NotNull(Assert.IsType<JoinAcceptedMessage>(delivered[0]).ReceivedTerrain);
    }

    // ---- Helpers --------------------------------------------------------------------------------

    private static JoinAcceptedMessage WithManifest(Func<TerrainTransferManifest, TerrainTransferManifest> change) =>
        Carrier(Terrain, Id) with { Terrain = change(Terrain.Manifest) };

    private static void FeedTerrain(WorldTransferReassembler reassembler)
    {
        foreach (TerrainChunkMessage fragment in Fragments(Terrain, Id))
        {
            reassembler.Accept(fragment, _ => { });
        }
    }

    private static List<IServerToClientMessage> FeedAll(JoinAcceptedMessage carrier, params IServerToClientMessage[] frames)
    {
        (WorldTransferReassembler reassembler, List<IServerToClientMessage> delivered) = Started(carrier);
        foreach (IServerToClientMessage frame in frames)
        {
            reassembler.Accept(frame, delivered.Add);
        }

        return delivered;
    }

    private static TerrainChunkMessage MutateFirstChunk(Func<PersistedTerrainChunk, PersistedTerrainChunk> change)
    {
        TerrainChunkMessage first = Fragments(Terrain, Id)[0];
        return first with { Chunks = [change(first.Chunks[0]), .. first.Chunks.Skip(1)] };
    }

    private static void AcceptFirst(TerrainChunkMessage fragment)
    {
        (WorldTransferReassembler reassembler, _) = Started(Carrier(Terrain, Id));
        reassembler.Accept(fragment, _ => { });
    }
}
