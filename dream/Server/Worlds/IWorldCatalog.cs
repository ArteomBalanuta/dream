using Dream.Simulation.Persistence;
using Dream.Simulation.World;

namespace Dream.Server.Worlds;

// The worlds stored in one database.
public interface IWorldCatalog
{
    // Every world, ORDER BY world_id.
    IReadOnlyList<WorldRecord> List();

    WorldRecord? TryGet(WorldId id);

    // Names compare ignoring case (NOCASE).
    WorldRecord? TryGetByName(string name);

    // Validates the options; generates the seed when it is null; created_schema_version is the
    // current schema version. Throws WorldNameTakenException on a name conflict.
    WorldRecord Create(WorldCreateOptions options);

    // In one owned transaction: a new world with every world-scoped row of source, the same local
    // ids, and the source's seed and world time.
    WorldRecord Copy(WorldId source, string newName);

    // In one owned transaction: an explicit DELETE per world-scoped table (children first), then
    // the world row. False when the world does not exist.
    bool Delete(WorldId id);

    // What export needs from a world: the record (a missing seed is generated and stored first, like
    // Copy), the stored chunks (null for a generated world) and the overrides in object_id order.
    // WorldNotFoundException when the world does not exist.
    WorldTerrainSource ReadTerrainSource(WorldId id);

    // One owned transaction: a new world with map_source 'stored', the source's bounds and provenance
    // generator, and the chunks. terrainOnly false: every world-scoped row Copy copies except
    // terrain_cell_overrides (the removals are baked into the chunks), with the same ids, counter,
    // seed and world time. terrainOnly true: no other rows, a new seed, world time 0 and
    // next_object_id 1. Throws WorldNotFoundException / WorldNameTakenException.
    WorldRecord CreateStored(WorldId source, string newName, IReadOnlyList<PersistedTerrainChunk> chunks, bool terrainOnly);

    // One owned transaction. For every override cell with no placed_cube_instances row in this world,
    // deletes the override and every cube_fracture_pieces row whose parent cell is that cell (any
    // origin, so the restore heal cannot remove the cell again). Cells under a placed cube keep their
    // override and rubble. WorldNotFoundException when the world does not exist.
    TerrainResetResult ResetTerrain(WorldId id);

    // Default: world 1, else the lowest id, else create "default" with newWorldSize.
    // Id: must exist (WorldNotFoundException).
    // Name: open it, or create it with newWorldSize.
    // created is true when this call created the world.
    WorldRecord Resolve(WorldSelector selector, int newWorldSize, out bool created);
}

public sealed record WorldTerrainSource(
    WorldRecord Record,
    ulong Seed,
    IReadOnlyList<PersistedTerrainChunk>? StoredChunks,
    IReadOnlyList<PersistedTerrainCellOverride> Overrides);

public readonly record struct TerrainResetResult(int OverridesCleared, int RubbleRowsDeleted, int CellsKeptForPlacedCubes);
