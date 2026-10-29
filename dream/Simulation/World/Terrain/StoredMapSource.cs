using Dream.Simulation.Persistence;
using Dream.Simulation.World.Geometry;
using Dream.Simulation.World.MapSources;

namespace Dream.Simulation.World.Terrain;

// A map source backed by terrain chunk blobs. Cells come back in canonical (x, z, y) ascending order,
// freshly decoded on every call (Map mutates the records it is given).
public sealed class StoredMapSource : IMapSource
{
    private readonly IReadOnlyList<PersistedTerrainChunk> _chunks;
    private readonly long _maxSubcellOverrides;
    private readonly long _maxImpliedSubcells;

    public StoredMapSource(MapDefinition definition, IReadOnlyList<PersistedTerrainChunk> chunks,
        long maxSubcellOverrides = long.MaxValue, long maxImpliedSubcells = long.MaxValue)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        ArgumentNullException.ThrowIfNull(chunks);
        int countX = TerrainChunkCodec.ChunkCountX(definition);
        int countZ = TerrainChunkCodec.ChunkCountZ(definition);
        if (chunks.Count != countX * countZ)
        {
            throw new InvalidDataException($"stored terrain has {chunks.Count} chunks, expected {countX * countZ}");
        }

        HashSet<(int, int)> seen = [];
        foreach (PersistedTerrainChunk chunk in chunks)
        {
            if (chunk.FormatVersion != TerrainChunkFormat.CurrentVersion)
            {
                throw new InvalidDataException($"unsupported terrain chunk format version {chunk.FormatVersion}");
            }

            if ((uint)chunk.ChunkX >= (uint)countX || (uint)chunk.ChunkZ >= (uint)countZ ||
                !seen.Add((chunk.ChunkX, chunk.ChunkZ)))
            {
                throw new InvalidDataException($"unexpected or duplicate terrain chunk ({chunk.ChunkX}, {chunk.ChunkZ})");
            }
        }

        _chunks = chunks;
        _maxSubcellOverrides = maxSubcellOverrides;
        _maxImpliedSubcells = maxImpliedSubcells;
    }

    public MapDefinition Definition { get; }

    public IReadOnlyList<GeometryCellRecord> CreateCells()
    {
        int depth = Definition.Depth;
        int height = Definition.Height;
        GeometryCellRecord?[] slots = new GeometryCellRecord?[Definition.Width * depth * height];
        int total = 0;
        long budget = _maxSubcellOverrides;
        long impliedBudget = _maxImpliedSubcells;
        foreach (PersistedTerrainChunk chunk in _chunks)
        {
            List<GeometryCellRecord> cells = TerrainChunkCodec.Decode(Definition, chunk.ChunkX, chunk.ChunkZ, chunk.Data, ref budget, ref impliedBudget);
            if (cells.Count != chunk.CellCount)
            {
                throw new InvalidDataException($"terrain chunk ({chunk.ChunkX}, {chunk.ChunkZ}) cell count differs from its blob");
            }

            foreach (GeometryCellRecord cell in cells)
            {
                slots[(((cell.Key.X * depth) + cell.Key.Z) * height) + cell.Key.Y] = cell;
            }

            total += cells.Count;
        }

        List<GeometryCellRecord> ordered = new(total);
        foreach (GeometryCellRecord? slot in slots)
        {
            if (slot != null)
            {
                ordered.Add(slot);
            }
        }

        return ordered;
    }
}
