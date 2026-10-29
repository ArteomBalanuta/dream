using Dream.Server.Persistence.Store;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Server.Persistence.Repositories;

// cube_fracture_pieces: the rubble left by fractured cubes, keyed by (source_cube_id, piece_index).
public sealed partial class SqliteRubbleRepository(IWorldStore store, WorldId worldId)
    : SqliteRepositoryBase(store, worldId), IRubbleRepository
{
    // World 1: the default world.
    public SqliteRubbleRepository(IWorldStore store)
        : this(store, WorldId.Default)
    {
    }

    private const string LoadSql = """
        SELECT
            source_cube_id,
            piece_index,
            owner_player_name,
            parent_cell_x,
            parent_cell_y,
            parent_cell_z,
            center_x,
            center_y,
            center_z,
            side_length,
            material_id,
            created_utc,
            updated_utc
        FROM cube_fracture_pieces
        WHERE world_id = $worldId
        ORDER BY source_cube_id, piece_index;
        """;

    private const string UpsertSql = """
        INSERT INTO cube_fracture_pieces (
            world_id,
            source_cube_id,
            piece_index,
            owner_player_name,
            parent_cell_x,
            parent_cell_y,
            parent_cell_z,
            center_x,
            center_y,
            center_z,
            side_length,
            material_id,
            created_utc,
            updated_utc
        )
        VALUES (
            $worldId,
            $sourceCubeId,
            $pieceIndex,
            $ownerPlayerName,
            $parentCellX,
            $parentCellY,
            $parentCellZ,
            $centerX,
            $centerY,
            $centerZ,
            $sideLength,
            $materialId,
            $createdUtc,
            $updatedUtc
        )
        ON CONFLICT(world_id, source_cube_id, piece_index) DO UPDATE SET
            owner_player_name = excluded.owner_player_name,
            parent_cell_x = excluded.parent_cell_x,
            parent_cell_y = excluded.parent_cell_y,
            parent_cell_z = excluded.parent_cell_z,
            center_x = excluded.center_x,
            center_y = excluded.center_y,
            center_z = excluded.center_z,
            side_length = excluded.side_length,
            material_id = excluded.material_id,
            updated_utc = excluded.updated_utc;
        """;

    private const string DeleteSql = "DELETE FROM cube_fracture_pieces WHERE world_id = $worldId AND source_cube_id = $sourceCubeId;";

    public IReadOnlyList<PersistedCubeFracturePiece> LoadCubeFracturePieces() =>
        QueryList(LoadSql, static reader => new PersistedCubeFracturePiece(
            ObjectId: ObjectId.None,
            OriginObjectId: new ObjectId(reader.GetInt64(0)),
            PieceIndex: reader.GetInt32(1),
            OwnerPlayerName: reader.GetString(2),
            ParentCell: reader.GetVector3(3),
            Center: reader.GetVector3(6),
            SideLength: reader.GetFloat(9),
            MaterialId: Enum.Parse<GeometryMaterialId>(reader.GetString(10), ignoreCase: true),
            CreatedUtc: reader.GetUtc(11),
            UpdatedUtc: reader.GetUtc(12)));

    // created_utc keeps piece.CreatedUtc (UtcNow when unset); updated_utc is the write time.
    public void SaveCubeFracturePiece(PersistedCubeFracturePiece piece) =>
        Execute(UpsertSql, piece, static (command, p) =>
        {
            command.BindInt64("$sourceCubeId", p.OriginObjectId.Value);
            command.BindInt64("$pieceIndex", p.PieceIndex);
            command.BindText("$ownerPlayerName", (p.OwnerPlayerName ?? string.Empty).Trim());
            command.BindVector3("$parentCellX", "$parentCellY", "$parentCellZ", p.ParentCell);
            command.BindVector3("$centerX", "$centerY", "$centerZ", p.Center);
            command.BindReal("$sideLength", p.SideLength);
            command.BindText("$materialId", p.MaterialId.ToString());
            command.BindUtc("$createdUtc", p.CreatedUtc == default ? DateTime.UtcNow : p.CreatedUtc);
            command.BindUtc("$updatedUtc", DateTime.UtcNow);
        });

    public void DeleteCubeFracturePieces(ObjectId originObjectId) =>
        Execute(DeleteSql, originObjectId, static (command, id) => command.BindInt64("$sourceCubeId", id.Value));
}
