using Dream.Multiplayer.Protocol;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Dream.Simulation.World.Terrain;

namespace Dream.Tests.Multiplayer.Protocol;

// Synthetic stored terrain for the wire tests: the blobs are random bytes (the transfer never
// decodes them), the layout is a real chunk grid.
internal static class TerrainTransferTestData
{
    public const int Size = 40; // 3 x 3 chunks
    public const int Height = 8;

    public static WorldDescriptor World(int width = Size, int depth = Size) =>
        new(new WorldId(1), "stored-test", width, Height, depth, WorldMapSource.Stored, "default", 1);

    public static List<PersistedTerrainChunk> Chunks(int width = Size, int depth = Size, int seed = 7, int minBytes = 1500, int maxBytes = 3000)
    {
        Random random = new(seed);
        List<PersistedTerrainChunk> chunks = [];
        int countX = (width + 15) / 16;
        int countZ = (depth + 15) / 16;
        // Stored order is arbitrary; the payload sorts.
        for (int z = countZ - 1; z >= 0; z--)
        {
            for (int x = 0; x < countX; x++)
            {
                byte[] data = new byte[random.Next(minBytes, maxBytes)];
                random.NextBytes(data);
                chunks.Add(new PersistedTerrainChunk(x, z, TerrainChunkFormat.CurrentVersion, random.Next(0, 2048), data));
            }
        }

        return chunks;
    }

    public static TerrainTransferPayload Payload(int fragmentRawBytes = 6000, int seed = 7) =>
        TerrainTransferPayload.Create(World(), Chunks(seed: seed), fragmentRawBytes);

    public static JoinAcceptedMessage Carrier(TerrainTransferPayload? payload, int? transferId, WorldSnapshot? initialWorld = null)
    {
        return new JoinAcceptedMessage("session-a", 1, 2, initialWorld, WorldTransferId: transferId, World: WorldDescriptorWire.From(World()), Terrain: payload?.Manifest)
        {
            TerrainPayload = payload
        };
    }

    public static List<TerrainChunkMessage> Fragments(TerrainTransferPayload payload, int transferId)
    {
        List<TerrainChunkMessage> fragments = [];
        for (int i = 0; i < payload.Fragments.Count; i++)
        {
            (int start, int length) = payload.Fragments[i].GetOffsetAndLength(payload.Chunks.Count);
            fragments.Add(new TerrainChunkMessage(
                transferId,
                i,
                i == payload.Fragments.Count - 1,
                payload.Chunks.Skip(start).Take(length).ToArray()));
        }

        return fragments;
    }

    public static WorldChunkMessage FinalWorldChunk(int transferId, int index = 0) =>
        new(transferId, index, true, new WorldSnapshot(2, 2, 2, [], FracturedCubeCells: []));

    public static WorldChunkMessage FirstOfTwoWorldChunks(int transferId) =>
        new(transferId, 0, false, new WorldSnapshot(2, 2, 2, [], FracturedCubeCells: []));

    // A reassembler holding the carrier of a fresh transfer.
    public static (WorldTransferReassembler Reassembler, List<IServerToClientMessage> Delivered) Started(
        JoinAcceptedMessage carrier,
        WorldTransferLimits? limits = null,
        TimeProvider? clock = null)
    {
        List<IServerToClientMessage> delivered = [];
        WorldTransferReassembler reassembler = new(limits ?? WorldTransferLimits.Default, clock);
        reassembler.Accept(carrier, delivered.Add);
        return (reassembler, delivered);
    }

    public static PersistedTerrainChunk WithData(PersistedTerrainChunk chunk, byte[] data) => chunk with { Data = data };
}
