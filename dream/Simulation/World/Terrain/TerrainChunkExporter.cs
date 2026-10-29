using Dream.Simulation.Persistence;
using Dream.Simulation.World.Geometry;
using Dream.Simulation.World.MapSources;

namespace Dream.Simulation.World.Terrain;

// Turns terrain into chunk blobs. Output covers the whole chunk grid (edge chunks included, empty
// chunks too) ordered by chunk_z, then chunk_x.
public static class TerrainChunkExporter
{
    public static IReadOnlyList<PersistedTerrainChunk> Export(IMapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Export(source.Definition, source.CreateCells());
    }

    // Exports the map's current base terrain: every cell that is still a source cell, so destroyed cells
    // are baked out and placed cubes and rubble are left out. Deviation from the plan text: the y == 0 floor
    // cells are exported too, because IsSourceCell requires Y > 0 and a stored world needs its floor.
    public static IReadOnlyList<PersistedTerrainChunk> Export(Map map)
    {
        ArgumentNullException.ThrowIfNull(map);
        List<GeometryCellRecord> cells = [];
        foreach (GeometryCellRecord record in map.GeometryCells.Values)
        {
            if (record.Key.Y == 0 ? record.Definition.IsFloor : map.IsSourceCell(record.Key))
            {
                cells.Add(record);
            }
        }

        return Export(map.Source.Definition, cells);
    }

    private static IReadOnlyList<PersistedTerrainChunk> Export(MapDefinition definition, IReadOnlyList<GeometryCellRecord> cells)
    {
        int countX = TerrainChunkCodec.ChunkCountX(definition);
        int countZ = TerrainChunkCodec.ChunkCountZ(definition);
        List<GeometryCellRecord>?[] buckets = new List<GeometryCellRecord>?[countX * countZ];
        foreach (GeometryCellRecord cell in cells)
        {
            int cx = cell.Key.X / TerrainChunkFormat.ChunkSize;
            int cz = cell.Key.Z / TerrainChunkFormat.ChunkSize;
            if (cell.Key.X < 0 || cell.Key.Z < 0 || cx >= countX || cz >= countZ)
            {
                throw new InvalidOperationException($"cell {cell.Key} is outside the world");
            }

            (buckets[(cz * countX) + cx] ??= []).Add(cell);
        }

        List<PersistedTerrainChunk> result = new(buckets.Length);
        for (int cz = 0; cz < countZ; cz++)
        {
            for (int cx = 0; cx < countX; cx++)
            {
                List<GeometryCellRecord> bucket = buckets[(cz * countX) + cx] ?? [];
                byte[] data = TerrainChunkCodec.Encode(definition, cx, cz, bucket);
                result.Add(new PersistedTerrainChunk(cx, cz, TerrainChunkFormat.CurrentVersion, bucket.Count, data));
            }
        }

        return result;
    }
}
