using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Dream.Simulation.World.MapSources;
using Dream.Simulation.World.Terrain;

namespace Dream.Multiplayer.Protocol;

// Server side, immutable, built once per host and shared by every join: the stored terrain of a
// world, sorted for the wire, split into fragments and described by a manifest.
public sealed class TerrainTransferPayload
{
    // About 64 KB after base64, like the world chunks.
    public const int DefaultFragmentRawBytes = 48 * 1024;

    private TerrainTransferPayload(
        IReadOnlyList<PersistedTerrainChunk> chunks,
        IReadOnlyList<Range> fragments,
        TerrainTransferManifest manifest)
    {
        Chunks = chunks;
        Fragments = fragments;
        Manifest = manifest;
    }

    // Sorted (chunk_z, chunk_x) ascending.
    public IReadOnlyList<PersistedTerrainChunk> Chunks { get; }

    // Index ranges into Chunks.
    public IReadOnlyList<Range> Fragments { get; }

    public TerrainTransferManifest Manifest { get; }

    // Throws InvalidDataException when the chunks do not cover the world exactly (see StoredMapSource), a
    // blob is over WorldTransferLimits.Default.MaxTerrainBlobBytes or the total over MaxTerrainBytes.
    // Fragments are greedy: whole blobs while the raw sum stays within fragmentRawBytes, at least one
    // blob each.
    public static TerrainTransferPayload Create(
        WorldDescriptor world,
        IReadOnlyList<PersistedTerrainChunk> chunks,
        int fragmentRawBytes = DefaultFragmentRawBytes)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentOutOfRangeException.ThrowIfLessThan(fragmentRawBytes, 1);
        if (world.MapSource != WorldMapSource.Stored)
        {
            throw new ArgumentException("terrain is transferred for stored worlds only", nameof(world));
        }

        // Coverage, format version and duplicate checks.
        _ = new StoredMapSource(MapDefinition.ForWorld(world), chunks);

        WorldTransferLimits limits = WorldTransferLimits.Default;
        long total = 0;
        foreach (PersistedTerrainChunk chunk in chunks)
        {
            if (chunk.Data == null)
            {
                throw new InvalidDataException($"terrain chunk ({chunk.ChunkX}, {chunk.ChunkZ}) has no data");
            }

            if (chunk.Data.Length > limits.MaxTerrainBlobBytes)
            {
                throw new InvalidDataException(
                    $"terrain chunk ({chunk.ChunkX}, {chunk.ChunkZ}) is {chunk.Data.Length} bytes, over the {limits.MaxTerrainBlobBytes} limit");
            }

            total += chunk.Data.Length;
        }

        if (total > limits.MaxTerrainBytes)
        {
            throw new InvalidDataException($"stored terrain is {total} bytes, over the {limits.MaxTerrainBytes} limit");
        }

        PersistedTerrainChunk[] sorted = [.. chunks];
        Array.Sort(sorted, static (a, b) =>
        {
            int byZ = a.ChunkZ.CompareTo(b.ChunkZ);
            return byZ != 0 ? byZ : a.ChunkX.CompareTo(b.ChunkX);
        });

        List<Range> fragments = [];
        int start = 0;
        long raw = 0;
        for (int i = 0; i < sorted.Length; i++)
        {
            if (i > start && raw + sorted[i].Data.Length > fragmentRawBytes)
            {
                fragments.Add(start..i);
                start = i;
                raw = 0;
            }

            raw += sorted[i].Data.Length;
        }

        if (sorted.Length > start)
        {
            fragments.Add(start..sorted.Length);
        }

        TerrainTransferManifest manifest = new(
            sorted.Length,
            fragments.Count,
            total,
            TerrainTransferDigest.Compute(sorted));
        return new TerrainTransferPayload(sorted, fragments, manifest);
    }
}
