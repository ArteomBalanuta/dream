using System.Text.Json;
using System.Text.Json.Serialization;
using Dream.Simulation.Persistence;

namespace Dream.Multiplayer.Protocol;

// Protocol v8: one fragment of the stored terrain of a world, sent after a join-accepted carrier whose
// "terrain" manifest announced it and before world chunk 0. Chunks are the stored blobs byte-for-byte
// (PersistedTerrainChunk), strictly ascending by (z, x) across all fragments of a transfer.
//
// Wire shape: {"transferId":n,"index":n,"chunks":[{"x":0,"z":0,"v":1,"n":312,"data":"<base64>"},...],"final":true?}
// Index counts terrain frames of the transfer only; "final" is written on the last fragment.
public sealed record TerrainChunkMessage(
    int TransferId,
    int Index,
    bool IsFinal,
    IReadOnlyList<PersistedTerrainChunk> Chunks) : IServerToClientMessage
{
    public const string Kind = "server.terrain-chunk";

    public string MessageKind => Kind;
}

internal sealed class TerrainChunkMessageJsonConverter : JsonConverter<TerrainChunkMessage>
{
    internal static readonly JsonEncodedText TransferId = JsonEncodedText.Encode("transferId");
    internal static readonly JsonEncodedText Index = JsonEncodedText.Encode("index");
    internal static readonly JsonEncodedText Chunks = JsonEncodedText.Encode("chunks");
    internal static readonly JsonEncodedText Final = JsonEncodedText.Encode("final");
    private static readonly JsonEncodedText X = JsonEncodedText.Encode("x");
    private static readonly JsonEncodedText Z = JsonEncodedText.Encode("z");
    private static readonly JsonEncodedText Version = JsonEncodedText.Encode("v");
    private static readonly JsonEncodedText CellCount = JsonEncodedText.Encode("n");
    private static readonly JsonEncodedText Data = JsonEncodedText.Encode("data");

    // The longest base64 text of a blob WorldTransferLimits.Default admits.
    private static readonly long MaxBase64Chars = 4L * ((WorldTransferLimits.Default.MaxTerrainBlobBytes + 2) / 3);

    public override TerrainChunkMessage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        HotPathJson.ExpectStartObject(ref reader, nameof(TerrainChunkMessage));
        int transferId = 0;
        int index = 0;
        bool isFinal = false;
        List<PersistedTerrainChunk>? chunks = null;
        while (HotPathJson.NextProperty(ref reader, nameof(TerrainChunkMessage)))
        {
            if (reader.ValueTextEquals(TransferId.EncodedUtf8Bytes))
            {
                transferId = HotPathJson.ReadInt32(ref reader);
            }
            else if (reader.ValueTextEquals(Index.EncodedUtf8Bytes))
            {
                index = HotPathJson.ReadInt32(ref reader);
            }
            else if (reader.ValueTextEquals(Final.EncodedUtf8Bytes))
            {
                isFinal = HotPathJson.ReadBoolean(ref reader);
            }
            else if (reader.ValueTextEquals(Chunks.EncodedUtf8Bytes))
            {
                chunks = ReadChunks(ref reader);
            }
            else
            {
                HotPathJson.SkipValue(ref reader);
            }
        }

        return new TerrainChunkMessage(
            transferId,
            index,
            isFinal,
            chunks ?? throw new JsonException("A terrain chunk message has no chunks."));
    }

    public override void Write(Utf8JsonWriter writer, TerrainChunkMessage value, JsonSerializerOptions options)
    {
        WriteFragment(writer, value.TransferId, value.Index, value.Chunks, 0, value.Chunks.Count, value.IsFinal);
    }

    // Writes the chunks [start, end) as one fragment payload.
    internal static void WriteFragment(
        Utf8JsonWriter writer,
        int transferId,
        int index,
        IReadOnlyList<PersistedTerrainChunk> chunks,
        int start,
        int end,
        bool isFinal)
    {
        writer.WriteStartObject();
        writer.WriteNumber(TransferId, transferId);
        writer.WriteNumber(Index, index);
        writer.WriteStartArray(Chunks);
        for (int i = start; i < end; i++)
        {
            PersistedTerrainChunk chunk = chunks[i];
            writer.WriteStartObject();
            writer.WriteNumber(X, chunk.ChunkX);
            writer.WriteNumber(Z, chunk.ChunkZ);
            writer.WriteNumber(Version, chunk.FormatVersion);
            writer.WriteNumber(CellCount, chunk.CellCount);
            writer.WriteBase64String(Data, chunk.Data);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        if (isFinal)
        {
            writer.WriteBoolean(Final, true);
        }

        writer.WriteEndObject();
    }

    private static List<PersistedTerrainChunk> ReadChunks(ref Utf8JsonReader reader)
    {
        HotPathJson.ReadValue(ref reader);
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException($"Expected an array of terrain chunks, found {reader.TokenType}.");
        }

        List<PersistedTerrainChunk> chunks = [];
        while (true)
        {
            if (!reader.Read())
            {
                throw new JsonException("Unexpected end of JSON while reading terrain chunks.");
            }

            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return chunks;
            }

            chunks.Add(ReadChunk(ref reader));
        }
    }

    private static PersistedTerrainChunk ReadChunk(ref Utf8JsonReader reader)
    {
        HotPathJson.ExpectStartObject(ref reader, nameof(PersistedTerrainChunk));
        int? x = null;
        int? z = null;
        int? version = null;
        int? cellCount = null;
        byte[]? data = null;
        while (HotPathJson.NextProperty(ref reader, nameof(PersistedTerrainChunk)))
        {
            if (reader.ValueTextEquals(X.EncodedUtf8Bytes))
            {
                x = HotPathJson.ReadInt32(ref reader);
            }
            else if (reader.ValueTextEquals(Z.EncodedUtf8Bytes))
            {
                z = HotPathJson.ReadInt32(ref reader);
            }
            else if (reader.ValueTextEquals(Version.EncodedUtf8Bytes))
            {
                version = HotPathJson.ReadInt32(ref reader);
            }
            else if (reader.ValueTextEquals(CellCount.EncodedUtf8Bytes))
            {
                cellCount = HotPathJson.ReadInt32(ref reader);
            }
            else if (reader.ValueTextEquals(Data.EncodedUtf8Bytes))
            {
                data = ReadData(ref reader);
            }
            else
            {
                HotPathJson.SkipValue(ref reader);
            }
        }

        if (x is null || z is null || version is null || cellCount is null || data is null)
        {
            throw new JsonException("A terrain chunk needs x, z, v, n and data.");
        }

        return new PersistedTerrainChunk(x.Value, z.Value, version.Value, cellCount.Value, data);
    }

    private static byte[] ReadData(ref Utf8JsonReader reader)
    {
        HotPathJson.ReadValue(ref reader);
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Expected a base64 string, found {reader.TokenType}.");
        }

        // WriteBase64String never escapes; an escaped spelling is not ours, and decoding it would
        // allocate before the length is known.
        if (reader.ValueIsEscaped)
        {
            throw new JsonException("A terrain chunk's data must not contain escape sequences.");
        }

        long length = reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;
        if (length > MaxBase64Chars)
        {
            throw new WorldTransferLimitException(
                $"A terrain chunk's data is {length} base64 characters, over the {MaxBase64Chars} limit.");
        }

        if (!reader.TryGetBytesFromBase64(out byte[]? bytes))
        {
            throw new JsonException("A terrain chunk's data is not valid base64.");
        }

        return bytes;
    }
}
