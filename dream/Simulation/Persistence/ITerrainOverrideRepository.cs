using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.Persistence;

// Replaced is reserved and never produced in wave D.
public enum TerrainCellOverrideKind
{
    Removed = 0,
    Replaced = 1
}

// A change to one cell of the generated base terrain: a destroyed terrain cell stays destroyed after
// a restart. The ObjectId is the origin of the rubble the destruction left.
public readonly record struct PersistedTerrainCellOverride(
    ObjectId ObjectId,
    GeometryCellKey Cell,
    TerrainCellOverrideKind Kind,
    byte[]? CellData,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);

public interface ITerrainOverrideRepository
{
    // ORDER BY object_id.
    IReadOnlyList<PersistedTerrainCellOverride> LoadTerrainCellOverrides();

    // Insert; an existing row for the cell wins (DO NOTHING).
    void SaveTerrainCellOverride(PersistedTerrainCellOverride cellOverride);
}
