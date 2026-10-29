using Dream.Server.Persistence.Store;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Server.Persistence.Repositories;

// terrain_cell_overrides: changes to the generated base terrain (a destroyed terrain cell stays
// destroyed). The id is the rubble origin id; one row per cell (unique index), first write wins.
public sealed class SqliteTerrainOverrideRepository(IWorldStore store, WorldId worldId)
    : SqliteRepositoryBase(store, worldId), ITerrainOverrideRepository
{
    private const string LoadSql = """
        SELECT object_id, cell_x, cell_y, cell_z, kind, cell_data, created_utc, updated_utc
        FROM terrain_cell_overrides
        WHERE world_id = $worldId
        ORDER BY object_id;
        """;

    // An existing row for the cell wins; the AFTER INSERT counter trigger does not fire for a
    // skipped insert, so a duplicate leaves worlds.next_object_id alone.
    private const string InsertSql = """
        INSERT INTO terrain_cell_overrides (
            world_id, object_id, cell_x, cell_y, cell_z, kind, cell_data, created_utc, updated_utc
        )
        VALUES (
            $worldId, $objectId, $cellX, $cellY, $cellZ, $kind, $cellData, $createdUtc, $updatedUtc
        )
        ON CONFLICT(world_id, cell_x, cell_y, cell_z) DO NOTHING;
        """;

    public IReadOnlyList<PersistedTerrainCellOverride> LoadTerrainCellOverrides() =>
        QueryList(LoadSql, static reader => new PersistedTerrainCellOverride(
            ObjectId: new ObjectId(reader.GetInt64(0)),
            Cell: new GeometryCellKey(reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3)),
            Kind: (TerrainCellOverrideKind)reader.GetInt32(4),
            CellData: reader.IsDBNull(5) ? null : (byte[])reader.GetValue(5),
            CreatedUtc: reader.GetUtc(6),
            UpdatedUtc: reader.GetUtc(7)));

    public void SaveTerrainCellOverride(PersistedTerrainCellOverride cellOverride) =>
        Execute(InsertSql, cellOverride, static (command, o) =>
        {
            command.BindInt64("$objectId", o.ObjectId.Value);
            command.BindInt64("$cellX", o.Cell.X);
            command.BindInt64("$cellY", o.Cell.Y);
            command.BindInt64("$cellZ", o.Cell.Z);
            command.BindInt64("$kind", (long)o.Kind);
            command.Parameters.AddWithValue("$cellData", (object?)o.CellData ?? DBNull.Value);
            command.BindUtc("$createdUtc", o.CreatedUtc == default ? DateTime.UtcNow : o.CreatedUtc);
            command.BindUtc("$updatedUtc", DateTime.UtcNow);
        });
}
