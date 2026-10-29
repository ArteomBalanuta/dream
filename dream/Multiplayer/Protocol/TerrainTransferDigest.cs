using System.Buffers.Binary;
using System.Security.Cryptography;
using Dream.Simulation.Persistence;

namespace Dream.Multiplayer.Protocol;

// SHA-256 over the terrain chunks of a transfer, in (chunk_z, chunk_x) ascending order. For each chunk:
// i32 x, i32 z, u16 formatVersion, i32 cellCount, i32 dataLength, data; all little-endian.
public static class TerrainTransferDigest
{
    // The chunks must already be in (chunk_z, chunk_x) ascending order. Returns lowercase hex.
    public static string Compute(IReadOnlyList<PersistedTerrainChunk> chunksInZxOrder)
    {
        ArgumentNullException.ThrowIfNull(chunksInZxOrder);
        using IncrementalHash hash = CreateIncremental();
        foreach (PersistedTerrainChunk chunk in chunksInZxOrder)
        {
            Append(hash, chunk);
        }

        return Finish(hash);
    }

    public static IncrementalHash CreateIncremental() => IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public static void Append(IncrementalHash hash, in PersistedTerrainChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(hash);
        Span<byte> header = stackalloc byte[18];
        BinaryPrimitives.WriteInt32LittleEndian(header, chunk.ChunkX);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], chunk.ChunkZ);
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..], checked((ushort)chunk.FormatVersion));
        BinaryPrimitives.WriteInt32LittleEndian(header[10..], chunk.CellCount);
        BinaryPrimitives.WriteInt32LittleEndian(header[14..], chunk.Data.Length);
        hash.AppendData(header);
        hash.AppendData(chunk.Data);
    }

    // Lowercase hex of the digest; resets the hash.
    public static string Finish(IncrementalHash hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
