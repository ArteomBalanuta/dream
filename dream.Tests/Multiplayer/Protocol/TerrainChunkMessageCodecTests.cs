using System.Text;
using System.Text.Json;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Simulation.Persistence;

namespace Dream.Tests.Multiplayer.Protocol;

// Protocol v8: server.terrain-chunk on the wire.
public sealed class TerrainChunkMessageCodecTests
{
    private static readonly TransportJsonCodec Codec = new();

    private static byte[] Encode(TerrainChunkMessage message, ulong sequence = 1) =>
        Codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, TerrainChunkMessage.Kind, sequence), message);

    private static string EncodeText(TerrainChunkMessage message) => Encoding.UTF8.GetString(Encode(message));

    private static TerrainChunkMessage Decode(string envelopeJson) =>
        Assert.IsType<TerrainChunkMessage>(Codec.Decode(Encoding.UTF8.GetBytes(envelopeJson)).Message);

    private static string Envelope(string payload) =>
        $"{{\"protocolVersion\":{TransportEnvelope.CurrentProtocolVersion},\"messageKind\":\"{TerrainChunkMessage.Kind}\",\"sequence\":1,\"payload\":{payload}}}";

    [Fact]
    public void Wire_IsTheDocumentedShape()
    {
        TerrainChunkMessage message = new(
            3,
            0,
            false,
            [new PersistedTerrainChunk(0, 0, 1, 312, [1, 2, 3, 4]), new PersistedTerrainChunk(1, 0, 1, 5, [9])]);

        Assert.Contains(
            "{\"transferId\":3,\"index\":0,\"chunks\":[{\"x\":0,\"z\":0,\"v\":1,\"n\":312,\"data\":\"AQIDBA==\"},{\"x\":1,\"z\":0,\"v\":1,\"n\":5,\"data\":\"CQ==\"}]}",
            EncodeText(message));
    }

    [Fact]
    public void FinalFragment_WritesFinalLast()
    {
        TerrainChunkMessage message = new(3, 4, true, [new PersistedTerrainChunk(2, 2, 1, 7, [1])]);

        Assert.Contains("\"data\":\"AQ==\"}],\"final\":true}", EncodeText(message));
        Assert.True(Decode(EncodeText(message)).IsFinal);
    }

    [Fact]
    public void RoundTrip_KeepsEveryChunkByteForByte()
    {
        TerrainTransferPayload payload = TerrainTransferTestData.Payload();
        foreach (TerrainChunkMessage fragment in TerrainTransferTestData.Fragments(payload, 9))
        {
            TerrainChunkMessage decoded = Decode(EncodeText(fragment));

            Assert.Equal(fragment.TransferId, decoded.TransferId);
            Assert.Equal(fragment.Index, decoded.Index);
            Assert.Equal(fragment.IsFinal, decoded.IsFinal);
            Assert.Equal(fragment.Chunks.Count, decoded.Chunks.Count);
            for (int i = 0; i < fragment.Chunks.Count; i++)
            {
                Assert.Equal(fragment.Chunks[i].ChunkX, decoded.Chunks[i].ChunkX);
                Assert.Equal(fragment.Chunks[i].ChunkZ, decoded.Chunks[i].ChunkZ);
                Assert.Equal(fragment.Chunks[i].FormatVersion, decoded.Chunks[i].FormatVersion);
                Assert.Equal(fragment.Chunks[i].CellCount, decoded.Chunks[i].CellCount);
                Assert.Equal(fragment.Chunks[i].Data, decoded.Chunks[i].Data);
            }
        }
    }

    // Standard base64 uses '+' and '/'; the writer must not turn '+' into \u002B.
    [Fact]
    public void Output_HasNoEscapedPlusAndNoBackslash()
    {
        byte[] data = [0xfb, 0xff, 0xfe, 0xfb, 0xef, 0xbe];
        string text = EncodeText(new TerrainChunkMessage(1, 0, true, [new PersistedTerrainChunk(0, 0, 1, 1, data)]));

        Assert.DoesNotContain("\\", text);
        Assert.DoesNotContain("u002B", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"data\":\"" + Convert.ToBase64String(data) + "\"", text);
        Assert.Contains("+", Convert.ToBase64String(data));
        Assert.Equal(data, Decode(text).Chunks[0].Data);
    }

    [Fact]
    public void EscapedData_IsRejected()
    {
        // "AQ==" spelled with an escape.
        string payload = "{\"transferId\":1,\"index\":0,\"chunks\":[{\"x\":0,\"z\":0,\"v\":1,\"n\":1,\"data\":\"\\u0041Q==\"}]}";

        Assert.Throws<JsonException>(() => Decode(Envelope(payload)));
    }

    [Fact]
    public void OversizeData_IsRejectedBeforeDecoding()
    {
        int limit = WorldTransferLimits.Default.MaxTerrainBlobBytes;
        string atLimit = new('A', 4 * ((limit + 2) / 3));
        string overLimit = new('A', (4 * ((limit + 2) / 3)) + 4);

        TerrainChunkMessage fits = Decode(Envelope(Chunk(atLimit)));
        Assert.Equal(3 * (atLimit.Length / 4), fits.Chunks[0].Data.Length);
        Assert.Throws<WorldTransferLimitException>(() => Decode(Envelope(Chunk(overLimit))));
    }

    [Theory]
    [InlineData("A")]
    [InlineData("AQ=")]
    [InlineData("AQ===")]
    [InlineData("AQ!=")]
    [InlineData("AQ==AQ==")]
    [InlineData("-_-_")]
    public void MalformedBase64_IsRejected(string data)
    {
        Assert.Throws<JsonException>(() => Decode(Envelope(Chunk(data))));
    }

    [Fact]
    public void DataThatIsNotAString_IsRejected()
    {
        Assert.Throws<JsonException>(() => Decode(Envelope(
            "{\"transferId\":1,\"index\":0,\"chunks\":[{\"x\":0,\"z\":0,\"v\":1,\"n\":1,\"data\":12}]}")));
    }

    [Theory]
    [InlineData("{\"transferId\":1,\"index\":0}")]
    [InlineData("{\"transferId\":1,\"index\":0,\"chunks\":{}}")]
    [InlineData("{\"transferId\":1,\"index\":0,\"chunks\":[{\"x\":0,\"z\":0,\"v\":1,\"data\":\"AQ==\"}]}")]
    [InlineData("{\"transferId\":1,\"index\":0,\"chunks\":[{\"z\":0,\"v\":1,\"n\":1,\"data\":\"AQ==\"}]}")]
    [InlineData("{\"transferId\":1,\"index\":0,\"chunks\":[5]}")]
    [InlineData("{\"transferId\":\"1\",\"index\":0,\"chunks\":[]}")]
    [InlineData("{\"transferId\":1,\"index\":0,\"chunks\":[{\"x\":0.5,\"z\":0,\"v\":1,\"n\":1,\"data\":\"AQ==\"}]}")]
    public void MalformedShapes_AreRejected(string payload)
    {
        Assert.Throws<JsonException>(() => Decode(Envelope(payload)));
    }

    [Fact]
    public void UnknownProperties_AreSkipped_AndAnEmptyFragmentDecodes()
    {
        TerrainChunkMessage decoded = Decode(Envelope("{\"extra\":{\"a\":[1,2]},\"transferId\":4,\"index\":2,\"chunks\":[]}"));

        Assert.Equal(4, decoded.TransferId);
        Assert.Equal(2, decoded.Index);
        Assert.False(decoded.IsFinal);
        Assert.Empty(decoded.Chunks);
    }

    [Fact]
    public void Kind_IsServerOnly()
    {
        Assert.True(TransportJsonCodec.IsServerOnlyKind(TerrainChunkMessage.Kind));
        Assert.Equal("server.terrain-chunk", TerrainChunkMessage.Kind);
    }

    private static string Chunk(string data) =>
        $"{{\"transferId\":1,\"index\":0,\"chunks\":[{{\"x\":0,\"z\":0,\"v\":1,\"n\":1,\"data\":\"{data}\"}}]}}";
}
