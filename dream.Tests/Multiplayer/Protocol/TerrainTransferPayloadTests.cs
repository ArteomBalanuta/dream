using System.Security.Cryptography;
using System.Text;
using Dream.Multiplayer.Protocol;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Dream.Simulation.World.MapSources;
using Dream.Simulation.World.Terrain;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Multiplayer.Protocol;

// Persistence wave E, C0: the terrain transfer contract types. The digest rule is pinned by a golden
// over a hand-built set; fragment plans, limits and order independence by fabricated blobs (the
// payload checks coverage, not blob contents).
public class TerrainTransferPayloadTests(ITestOutputHelper output)
{
    private static readonly TransportJsonCodec Codec = new();

    private static WorldDescriptor Stored(int size) =>
        WorldDescriptor.ForFloorSize(size) with { MapSource = WorldMapSource.Stored };

    // One fabricated chunk per grid cell of a size x size world, in (z, x) order, with the given blob sizes.
    private static List<PersistedTerrainChunk> Fabricate(int size, params int[] blobSizes)
    {
        int perSide = (size + 15) / 16;
        List<PersistedTerrainChunk> chunks = [];
        int n = 0;
        for (int z = 0; z < perSide; z++)
        {
            for (int x = 0; x < perSide; x++)
            {
                byte[] data = new byte[blobSizes[n % blobSizes.Length]];
                Array.Fill(data, (byte)(n + 1));
                chunks.Add(new PersistedTerrainChunk(x, z, TerrainChunkFormat.CurrentVersion, n + 1, data));
                n++;
            }
        }

        return chunks;
    }

    [Fact]
    public void Digest_GoldenForAHandBuiltTwoChunkSet_AndMatchesTheRuleByHand()
    {
        PersistedTerrainChunk[] set =
        [
            new PersistedTerrainChunk(0, 0, 1, 3, [1, 2, 3]),
            new PersistedTerrainChunk(1, 0, 1, 0, [])
        ];

        string digest = TerrainTransferDigest.Compute(set);

        // i32 x, i32 z, u16 formatVersion, i32 cellCount, i32 dataLength, data; little-endian.
        byte[] manual = Convert.FromHexString(
            "00000000" + "00000000" + "0100" + "03000000" + "03000000" + "010203" +
            "01000000" + "00000000" + "0100" + "00000000" + "00000000");
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(manual)), digest);
        Assert.Equal("a597f05513aa6f1ac76d1199487e765eb42e16806c02333e4cb4d8ae678a2482", digest);
        Assert.Matches("^[0-9a-f]{64}$", digest);
    }

    [Fact]
    public void Digest_Incremental_EqualsCompute_AndDependsOnEveryField()
    {
        List<PersistedTerrainChunk> chunks = Fabricate(40, 5, 9, 1);
        using var hash = TerrainTransferDigest.CreateIncremental();
        foreach (PersistedTerrainChunk chunk in chunks)
        {
            TerrainTransferDigest.Append(hash, chunk);
        }

        Assert.Equal(TerrainTransferDigest.Compute(chunks), TerrainTransferDigest.Finish(hash));

        string baseline = TerrainTransferDigest.Compute(chunks);
        PersistedTerrainChunk first = chunks[0];
        foreach (PersistedTerrainChunk changed in new[]
        {
            first with { ChunkX = 7 },
            first with { ChunkZ = 7 },
            first with { FormatVersion = 2 },
            first with { CellCount = first.CellCount + 1 },
            first with { Data = [.. first.Data, (byte)0] },
        })
        {
            List<PersistedTerrainChunk> copy = [.. chunks];
            copy[0] = changed;
            Assert.NotEqual(baseline, TerrainTransferDigest.Compute(copy));
        }
    }

    [Fact]
    public void Fragments_ExactFit_PacksWholeBlobsUpToTheLimit()
    {
        // 20x20 = 4 chunks of 50 B; a 100 B fragment holds exactly two.
        TerrainTransferPayload payload = TerrainTransferPayload.Create(Stored(20), Fabricate(20, 50), fragmentRawBytes: 100);

        Assert.Equal(new[] { 0..2, 2..4 }, payload.Fragments);
        Assert.Equal(new TerrainTransferManifest(4, 2, 200, TerrainTransferDigest.Compute(payload.Chunks)), payload.Manifest);
    }

    [Fact]
    public void Fragments_AnOversizeSingleBlob_GetsAFragmentOfItsOwn_AndOneOneByteOverSplits()
    {
        TerrainTransferPayload oversize = TerrainTransferPayload.Create(Stored(20), Fabricate(20, 10, 300, 10, 10), fragmentRawBytes: 100);
        Assert.Equal(new[] { 0..1, 1..2, 2..4 }, oversize.Fragments);

        TerrainTransferPayload overByOne = TerrainTransferPayload.Create(Stored(20), Fabricate(20, 50, 51, 50, 50), fragmentRawBytes: 100);
        Assert.Equal(new[] { 0..1, 1..2, 2..4 }, overByOne.Fragments);
    }

    [Fact]
    public void Fragments_CoverEveryChunkOnce_InOrder_AndAreFewForWorld10k()
    {
        MapDefinition definition = new(100, 8, 100, WorldMapSource.Generated, WorldGeneratorCatalog.DefaultKey, WorldGeneratorCatalog.DefaultVersion);
        IReadOnlyList<PersistedTerrainChunk> exported = TerrainChunkExporter.Export(new GeneratedMapSource(definition));

        TerrainTransferPayload payload = TerrainTransferPayload.Create(Stored(100), exported);

        output.WriteLine(
            $"World10k terrain: {payload.Manifest.Chunks} chunks, {payload.Manifest.Fragments} fragments, {payload.Manifest.Bytes:N0} B");
        Assert.True(payload.Fragments.Count <= 6);
        Assert.Equal(payload.Fragments.Count, payload.Manifest.Fragments);
        Assert.Equal(49, payload.Manifest.Chunks);
        Assert.Equal(exported.Sum(c => (long)c.Data.Length), payload.Manifest.Bytes);
        int next = 0;
        foreach (Range fragment in payload.Fragments)
        {
            Assert.Equal(next, fragment.Start.Value);
            Assert.True(fragment.End.Value > fragment.Start.Value);
            next = fragment.End.Value;
        }

        Assert.Equal(payload.Chunks.Count, next);
        Assert.True(payload.Chunks.Zip(payload.Chunks.Skip(1)).All(p => (p.First.ChunkZ, p.First.ChunkX).CompareTo((p.Second.ChunkZ, p.Second.ChunkX)) < 0));
    }

    [Fact]
    public void Create_IsIndependentOfTheInputOrder()
    {
        List<PersistedTerrainChunk> ordered = Fabricate(40, 7, 11, 13);
        List<PersistedTerrainChunk> shuffled = [.. ordered.OrderBy(c => (c.ChunkX * 31) ^ (c.ChunkZ * 17)).Reverse()];
        Assert.False(ordered.SequenceEqual(shuffled));

        TerrainTransferPayload a = TerrainTransferPayload.Create(Stored(40), ordered, 20);
        TerrainTransferPayload b = TerrainTransferPayload.Create(Stored(40), shuffled, 20);

        Assert.Equal(a.Manifest, b.Manifest);
        Assert.Equal(a.Fragments, b.Fragments);
        Assert.True(a.Chunks.Select(c => (c.ChunkX, c.ChunkZ)).SequenceEqual(b.Chunks.Select(c => (c.ChunkX, c.ChunkZ))));
        Assert.True(a.Chunks.Select(c => (c.ChunkZ, c.ChunkX)).SequenceEqual(ordered.Select(c => (c.ChunkZ, c.ChunkX))));
    }

    [Fact]
    public void Create_RejectsTheInputsTheLimitsAndTheCoverageRulesForbid()
    {
        WorldTransferLimits limits = WorldTransferLimits.Default;

        // A generated world has no terrain to transfer.
        Assert.Throws<ArgumentException>(() => TerrainTransferPayload.Create(WorldDescriptor.ForFloorSize(20), Fabricate(20, 5)));

        // Coverage: missing, duplicate, out of grid and wrong format version.
        List<PersistedTerrainChunk> chunks = Fabricate(20, 5);
        Assert.Throws<InvalidDataException>(() => TerrainTransferPayload.Create(Stored(20), chunks.Take(3).ToList()));
        List<PersistedTerrainChunk> duplicate = [.. chunks];
        duplicate[3] = duplicate[2];
        Assert.Throws<InvalidDataException>(() => TerrainTransferPayload.Create(Stored(20), duplicate));
        List<PersistedTerrainChunk> outside = [.. chunks];
        outside[3] = outside[3] with { ChunkX = 5 };
        Assert.Throws<InvalidDataException>(() => TerrainTransferPayload.Create(Stored(20), outside));
        List<PersistedTerrainChunk> oldFormat = [.. chunks];
        oldFormat[0] = oldFormat[0] with { FormatVersion = 99 };
        Assert.Throws<InvalidDataException>(() => TerrainTransferPayload.Create(Stored(20), oldFormat));

        // One blob over the per-blob limit; exactly at it is fine.
        List<PersistedTerrainChunk> bigBlob = Fabricate(20, 5);
        bigBlob[1] = bigBlob[1] with { Data = new byte[limits.MaxTerrainBlobBytes + 1] };
        Assert.Throws<InvalidDataException>(() => TerrainTransferPayload.Create(Stored(20), bigBlob));
        bigBlob[1] = bigBlob[1] with { Data = new byte[limits.MaxTerrainBlobBytes] };
        TerrainTransferPayload atLimit = TerrainTransferPayload.Create(Stored(20), bigBlob);
        Assert.Equal(limits.MaxTerrainBlobBytes + 15, atLimit.Manifest.Bytes);

        // 49 blobs of 400,000 B are each fine but 19.6 MB in total, over the 16 MiB total.
        InvalidDataException total = Assert.Throws<InvalidDataException>(
            () => TerrainTransferPayload.Create(Stored(100), Fabricate(100, 400_000)));
        Assert.Contains("over", total.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_WireShape_IsCamelCase_AndLowercaseHex()
    {
        TerrainTransferPayload payload = TerrainTransferPayload.Create(Stored(20), Fabricate(20, 5));
        JoinAcceptedMessage join = new("session-a", 101, 7, InitialWorld: null, Terrain: payload.Manifest);

        string json = Encode(join);

        Assert.Contains(
            $"\"terrain\":{{\"chunks\":4,\"fragments\":1,\"bytes\":20,\"sha256\":\"{payload.Manifest.Sha256}\"}}",
            json,
            StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{64}$", payload.Manifest.Sha256);
        JoinAcceptedMessage decoded = Assert.IsType<JoinAcceptedMessage>(Codec.Decode(Encoding.UTF8.GetBytes(json)).Message);
        Assert.Equal(payload.Manifest, decoded.Terrain);
        Assert.Null(decoded.TerrainPayload);
        Assert.Null(decoded.ReceivedTerrain);
    }

    [Fact]
    public void AJoinAccepted_WithTheNonWireTerrainProperties_EncodesByteIdenticalToOneWithout()
    {
        TerrainTransferPayload payload = TerrainTransferPayload.Create(Stored(20), Fabricate(20, 5));
        JoinAcceptedMessage plain = new(
            "session-a", 101, 7, InitialWorld: null, World: WorldDescriptorWire.From(Stored(20)));
        JoinAcceptedMessage withProperties = plain with { TerrainPayload = payload, ReceivedTerrain = payload.Chunks };

        Assert.Equal(Encode(plain), Encode(withProperties));
        Assert.DoesNotContain("terrain", Encode(plain), StringComparison.OrdinalIgnoreCase);

        JoinAcceptedMessage withManifest = plain with { Terrain = payload.Manifest };
        Assert.Equal(
            Encode(withManifest),
            Encode(withManifest with { TerrainPayload = payload, ReceivedTerrain = payload.Chunks }));
    }

    [Fact]
    public void Limits_HaveTheContractDefaults()
    {
        WorldTransferLimits limits = WorldTransferLimits.Default;

        Assert.Equal(16L << 20, limits.MaxTerrainBytes);
        Assert.Equal(1 << 20, limits.MaxTerrainBlobBytes);
        Assert.Equal(1L << 20, limits.MaxTerrainSubcellOverrides);
        Assert.Equal(48 * 1024, TerrainTransferPayload.DefaultFragmentRawBytes);
    }

    private static string Encode(JoinAcceptedMessage join) =>
        Encoding.UTF8.GetString(Codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, join.MessageKind, 1), join));
}
