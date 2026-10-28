using Dream.Server.Persistence.Store;
using Dream.Simulation.Persistence;
using Dream.Simulation.World.Geometry;

namespace Dream.Server.Persistence.Repositories;

// placed_cube_instances: the player-built cubes.
public sealed partial class SqlitePlacedObjectRepository(IWorldStore store)
    : SqliteRepositoryBase(store), IPlacedObjectRepository
{
    private const string LoadSql = """
        SELECT
            cube_id,
            owner_player_name,
            cell_x,
            cell_y,
            cell_z,
            center_x,
            center_y,
            center_z,
            shape_kind,
            material_id,
            current_health,
            max_health,
            created_utc,
            updated_utc
        FROM placed_cube_instances
        ORDER BY cube_id;
        """;

    private const string UpsertSql = """
        INSERT INTO placed_cube_instances (
            cube_id,
            owner_player_name,
            cell_x,
            cell_y,
            cell_z,
            center_x,
            center_y,
            center_z,
            shape_kind,
            material_id,
            current_health,
            max_health,
            created_utc,
            updated_utc
        )
        VALUES (
            $cubeId,
            $ownerPlayerName,
            $cellX,
            $cellY,
            $cellZ,
            $centerX,
            $centerY,
            $centerZ,
            $shapeKind,
            $materialId,
            $currentHealth,
            $maxHealth,
            $createdUtc,
            $updatedUtc
        )
        ON CONFLICT(cube_id) DO UPDATE SET
            owner_player_name = excluded.owner_player_name,
            cell_x = excluded.cell_x,
            cell_y = excluded.cell_y,
            cell_z = excluded.cell_z,
            center_x = excluded.center_x,
            center_y = excluded.center_y,
            center_z = excluded.center_z,
            shape_kind = excluded.shape_kind,
            material_id = excluded.material_id,
            current_health = excluded.current_health,
            max_health = excluded.max_health,
            updated_utc = excluded.updated_utc;
        """;

    private const string DeleteSql = "DELETE FROM placed_cube_instances WHERE cube_id = $cubeId;";

    public IReadOnlyList<PersistedPlacedCube> LoadPlacedCubes() =>
        QueryList(LoadSql, static reader => new PersistedPlacedCube(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetVector3(2),
            ShapeKind: Enum.Parse<GeometryShapeKind>(reader.GetString(8), ignoreCase: true),
            MaterialId: Enum.Parse<GeometryMaterialId>(reader.GetString(9), ignoreCase: true),
            CurrentHealth: reader.GetInt32(10),
            MaxHealth: reader.GetInt32(11),
            CreatedUtc: reader.GetUtc(12),
            UpdatedUtc: reader.GetUtc(13),
            CenterPosition: reader.GetNullableVector3(5)));

    // created_utc keeps cube.CreatedUtc (UtcNow when unset); updated_utc is the write time.
    public void SavePlacedCube(PersistedPlacedCube cube) =>
        Execute(UpsertSql, cube, static (command, c) =>
        {
            command.BindInt64("$cubeId", c.CubeId);
            command.BindText("$ownerPlayerName", c.OwnerPlayerName);
            command.BindVector3("$cellX", "$cellY", "$cellZ", c.CellPosition);
            command.BindNullableVector3("$centerX", "$centerY", "$centerZ", c.CenterPosition);
            command.BindText("$shapeKind", c.ShapeKind.ToString());
            command.BindText("$materialId", c.MaterialId.ToString());
            command.BindInt64("$currentHealth", c.CurrentHealth);
            command.BindInt64("$maxHealth", c.MaxHealth);
            command.BindUtc("$createdUtc", c.CreatedUtc == default ? DateTime.UtcNow : c.CreatedUtc);
            command.BindUtc("$updatedUtc", DateTime.UtcNow);
        });

    public void DeletePlacedCube(int cubeId) =>
        Execute(DeleteSql, cubeId, static (command, id) => command.BindInt64("$cubeId", id));
}
