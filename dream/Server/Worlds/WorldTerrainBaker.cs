using Dream.Multiplayer.Protocol;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Dream.Simulation.World.MapSources;
using Dream.Simulation.World.Terrain;

namespace Dream.Server.Worlds;

// Bakes a world's current base terrain (its source minus the Removed overrides) into chunk blobs for
// a new stored world. Deterministic: the same source state gives identical bytes.
public sealed record BakedTerrain(IReadOnlyList<PersistedTerrainChunk> Chunks, int RemovedCells, int SkippedOverrides, long Bytes);

public static class WorldTerrainBaker
{
    // Throws InvalidDataException when the chunks break the hosting limits (TerrainTransferPayload.Create).
    public static BakedTerrain Bake(
        WorldRecord source,
        ulong seed,
        IReadOnlyList<PersistedTerrainChunk>? storedChunks,
        IReadOnlyList<PersistedTerrainCellOverride> overrides)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(overrides);
        WorldDescriptor d = source.Descriptor;
        Map map = new(MapSources.ForWorld(d, seed, storedChunks), physics: null, headless: true);

        int removed = 0;
        int skipped = 0;
        foreach (PersistedTerrainCellOverride cellOverride in overrides.OrderBy(o => o.ObjectId.Value))
        {
            if (cellOverride.Kind != TerrainCellOverrideKind.Removed)
            {
                skipped++;
            }
            else if (map.RemoveSourceCell(cellOverride.Cell))
            {
                removed++;
            }
            else
            {
                skipped++;
            }
        }

        IReadOnlyList<PersistedTerrainChunk> chunks = TerrainChunkExporter.Export(map);
        WorldDescriptor stored = d with { MapSource = WorldMapSource.Stored };
        TerrainTransferPayload payload = TerrainTransferPayload.Create(stored, chunks);
        return new BakedTerrain(chunks, removed, skipped, payload.Manifest.Bytes);
    }
}
