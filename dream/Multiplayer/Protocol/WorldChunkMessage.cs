using System.Text.Json;
using System.Text.Json.Serialization;
using Dream.Simulation.World;

namespace Dream.Multiplayer.Protocol;

// One piece of a chunked world transfer (protocol v3, SCALE-1). A join or resync world no longer
// travels inside its JoinAccepted or server.snapshot: the server sends that message first with
// WorldTransferId set and no world (the carrier), then the world as server.world-chunk messages
// of bounded size, interleaved with the regular traffic that follows. Each chunk's World is a
// self-contained fragment in the compact format (WorldSnapshotJsonConverter) carrying the whole
// world's dimensions and mutation sequence. The client holds the carrier and everything after it,
// appends the fragments in order, and on the final chunk hands on the carrier with the assembled
// world followed by the held messages, so its consumers see exactly the v2 message order.
//
// Wire shape: {"transferId":n,"chunkIndex":n,"world":{compact world},"final":true?}
public sealed record WorldChunkMessage(
    int TransferId,
    int ChunkIndex,
    bool IsFinal,
    WorldSnapshot World) : IServerToClientMessage
{
    public const string Kind = "server.world-chunk";

    // A chunk's world fragment stops after the item that brings it to this many bytes, so a chunk
    // frame stays near 64 KB however large the world is. Sized from the server's send-stall
    // allowance (ServerConnectionOptions): a frame must finish within SendStallTimeout (5 s) plus
    // its bytes at SendStallMinimumBytesPerSecond (256 KB/s), so a 64 KB chunk survives links down
    // to about 12.5 KB/s, where the earlier 256 KB chunks faulted any link under about 43.7 KB/s
    // mid-resync. The cost is small: each fragment repeats the dimensions and restarts its delta
    // bases (tens of bytes), and the writer sends one queued message per chunk.
    public const int DefaultChunkBytes = 64 * 1024;

    public string MessageKind => Kind;
}

internal sealed class WorldChunkMessageJsonConverter : JsonConverter<WorldChunkMessage>
{
    internal static readonly JsonEncodedText TransferId = JsonEncodedText.Encode("transferId");
    internal static readonly JsonEncodedText ChunkIndex = JsonEncodedText.Encode("chunkIndex");
    internal static readonly JsonEncodedText World = JsonEncodedText.Encode("world");
    internal static readonly JsonEncodedText Final = JsonEncodedText.Encode("final");

    public override WorldChunkMessage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        HotPathJson.ExpectStartObject(ref reader, nameof(WorldChunkMessage));
        int transferId = 0;
        int chunkIndex = 0;
        bool isFinal = false;
        WorldSnapshot? world = null;
        while (HotPathJson.NextProperty(ref reader, nameof(WorldChunkMessage)))
        {
            if (reader.ValueTextEquals(TransferId.EncodedUtf8Bytes))
            {
                transferId = HotPathJson.ReadInt32(ref reader);
            }
            else if (reader.ValueTextEquals(ChunkIndex.EncodedUtf8Bytes))
            {
                chunkIndex = HotPathJson.ReadInt32(ref reader);
            }
            else if (reader.ValueTextEquals(Final.EncodedUtf8Bytes))
            {
                isFinal = HotPathJson.ReadBoolean(ref reader);
            }
            else if (reader.ValueTextEquals(World.EncodedUtf8Bytes))
            {
                HotPathJson.ReadValue(ref reader);
                world = WorldSnapshotJsonConverter.ReadWorld(ref reader);
            }
            else
            {
                HotPathJson.SkipValue(ref reader);
            }
        }

        return new WorldChunkMessage(
            transferId,
            chunkIndex,
            isFinal,
            world ?? throw new JsonException("A world chunk has no world fragment."));
    }

    public override void Write(Utf8JsonWriter writer, WorldChunkMessage value, JsonSerializerOptions options)
    {
        bool temporary = !WorldSnapshotCapture.TryGetCapture(value.World, out WorldSnapshotCapture? capture);
        capture ??= WorldSnapshotCapture.FromSnapshot(value.World);
        try
        {
            WorldSnapshotJsonConverter.Cursor cursor = default;
            WriteChunk(writer, value.TransferId, value.ChunkIndex, capture, ref cursor, long.MaxValue, forceFinal: value.IsFinal);
        }
        finally
        {
            if (temporary)
            {
                capture.Release();
            }
        }
    }

    // Writes one chunk payload from cursor onward (see WorldSnapshotJsonConverter.WriteWorld for
    // the budget). "final" is written after the fragment, once it is known whether the fragment
    // reached the end of the capture. Returns true for the final chunk.
    internal static bool WriteChunk(
        Utf8JsonWriter writer,
        int transferId,
        int chunkIndex,
        WorldSnapshotCapture capture,
        ref WorldSnapshotJsonConverter.Cursor cursor,
        long byteBudget,
        bool? forceFinal = null)
    {
        writer.WriteStartObject();
        writer.WriteNumber(TransferId, transferId);
        writer.WriteNumber(ChunkIndex, chunkIndex);
        writer.WritePropertyName(World);
        bool complete = WorldSnapshotJsonConverter.WriteWorld(writer, capture, ref cursor, byteBudget);
        bool isFinal = forceFinal ?? complete;
        if (isFinal)
        {
            writer.WriteBoolean(Final, true);
        }

        writer.WriteEndObject();
        return isFinal;
    }
}
