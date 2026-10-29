using Dream.Server.Persistence.Store;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Microsoft.Data.Sqlite;

namespace Dream.Server.Persistence.Repositories;

// The checkpoint write of placed_cube_instances, run by SqlitePersistenceCheckpointWriter inside
// its owned transaction.
public sealed partial class SqlitePlacedObjectRepository
{
    // Update only: the write may land after the queue deleted the cube, which must stay deleted.
    internal const string CheckpointUpdateSql = """
        UPDATE placed_cube_instances SET
            cell_x = $cellX,
            cell_y = $cellY,
            cell_z = $cellZ,
            center_x = $centerX,
            center_y = $centerY,
            center_z = $centerZ,
            material_id = $materialId,
            current_health = $currentHealth,
            max_health = $maxHealth,
            updated_utc = $updatedUtc
        WHERE world_id = $worldId AND cube_id = $cubeId;
        """;

    // One prepared command for all rows, its parameters set per row. writeUtc is the formatted
    // write time every row is stamped with.
    internal static void CheckpointUpdate(SqliteSession session, WorldId worldId, IReadOnlyList<PersistedPlacedCube> cubes, string writeUtc)
    {
        if (cubes.Count == 0)
        {
            return;
        }

        using SqliteCommand command = session.CreateCommand(SqliteRepositorySql.WithCheckpointLiterals(CheckpointUpdateSql, worldId, writeUtc));
        SqliteParameter cubeId = command.AddParameter("$cubeId", SqliteType.Integer);
        SqliteParameter cellX = command.AddParameter("$cellX", SqliteType.Real);
        SqliteParameter cellY = command.AddParameter("$cellY", SqliteType.Real);
        SqliteParameter cellZ = command.AddParameter("$cellZ", SqliteType.Real);
        SqliteParameter centerX = command.AddParameter("$centerX", SqliteType.Real);
        SqliteParameter centerY = command.AddParameter("$centerY", SqliteType.Real);
        SqliteParameter centerZ = command.AddParameter("$centerZ", SqliteType.Real);
        SqliteParameter materialId = command.AddParameter("$materialId", SqliteType.Text);
        SqliteParameter currentHealth = command.AddParameter("$currentHealth", SqliteType.Integer);
        SqliteParameter maxHealth = command.AddParameter("$maxHealth", SqliteType.Integer);

        foreach (PersistedPlacedCube cube in cubes)
        {
            cubeId.Value = cube.CubeId;
            cellX.Value = cube.CellPosition.X;
            cellY.Value = cube.CellPosition.Y;
            cellZ.Value = cube.CellPosition.Z;
            centerX.Value = cube.CenterPosition.HasValue ? cube.CenterPosition.Value.X : DBNull.Value;
            centerY.Value = cube.CenterPosition.HasValue ? cube.CenterPosition.Value.Y : DBNull.Value;
            centerZ.Value = cube.CenterPosition.HasValue ? cube.CenterPosition.Value.Z : DBNull.Value;
            materialId.Value = cube.MaterialId.ToString();
            currentHealth.Value = cube.CurrentHealth;
            maxHealth.Value = cube.MaxHealth;
            command.ExecuteNonQuery();
        }
    }
}
