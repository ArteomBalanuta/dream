using Dream.Server.Persistence.Store;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Server.Persistence.Repositories;

// cube_fracture_pieces: the rubble left by fractured cubes, keyed by object_id (origin_object_id and piece_index are unique per world).
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
            object_id,
            origin_object_id,
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
        ORDER BY origin_object_id, piece_index;
        """;

    private const string UpsertSql = """
        INSERT INTO cube_fracture_pieces (
            world_id,
            object_id,
            origin_object_id,
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
            $objectId,
            $originObjectId,
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
        ON CONFLICT(world_id, object_id) DO UPDATE SET
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

    // C0 shim, removed by the wave C glue: the bridge still passes ObjectId.None for a piece. Such a
    // piece takes the world's next id in SQL (the insert trigger then advances the counter) and
    // upserts on its (origin, index) key.
    private static readonly string UpsertByIndexSql = UpsertSql
        .Replace("$objectId,", "(SELECT next_object_id FROM worlds WHERE world_id = $worldId),", StringComparison.Ordinal)
        .Replace("ON CONFLICT(world_id, object_id) DO UPDATE SET", "ON CONFLICT(world_id, origin_object_id, piece_index) DO UPDATE SET", StringComparison.Ordinal);

    private const string DeleteSql = "DELETE FROM cube_fracture_pieces WHERE world_id = $worldId AND origin_object_id = $originObjectId;";

    public IReadOnlyList<PersistedCubeFracturePiece> LoadCubeFracturePieces() =>
        QueryList(LoadSql, static reader => new PersistedCubeFracturePiece(
            ObjectId: new ObjectId(reader.GetInt64(0)),
            OriginObjectId: new ObjectId(reader.GetInt64(1)),
            PieceIndex: reader.GetInt32(2),
            OwnerPlayerName: reader.GetNullableString(3),
            ParentCell: reader.GetVector3(4),
            Center: reader.GetVector3(7),
            SideLength: reader.GetFloat(10),
            MaterialId: Enum.Parse<GeometryMaterialId>(reader.GetString(11), ignoreCase: true),
            CreatedUtc: reader.GetUtc(12),
            UpdatedUtc: reader.GetUtc(13)));

    // created_utc keeps piece.CreatedUtc (UtcNow when unset); updated_utc is the write time.
    public void SaveCubeFracturePiece(PersistedCubeFracturePiece piece) =>
        Execute(piece.ObjectId.IsNone ? UpsertByIndexSql : UpsertSql, piece, static (command, p) =>
        {
            if (!p.ObjectId.IsNone)
            {
                command.BindInt64("$objectId", p.ObjectId.Value);
            }

            command.BindInt64("$originObjectId", p.OriginObjectId.Value);
            command.BindInt64("$pieceIndex", p.PieceIndex);
            command.BindNullableText("$ownerPlayerName", string.IsNullOrWhiteSpace(p.OwnerPlayerName) ? null : p.OwnerPlayerName.Trim());
            command.BindVector3("$parentCellX", "$parentCellY", "$parentCellZ", p.ParentCell);
            command.BindVector3("$centerX", "$centerY", "$centerZ", p.Center);
            command.BindReal("$sideLength", p.SideLength);
            command.BindText("$materialId", p.MaterialId.ToString());
            command.BindUtc("$createdUtc", p.CreatedUtc == default ? DateTime.UtcNow : p.CreatedUtc);
            command.BindUtc("$updatedUtc", DateTime.UtcNow);
        });

    public void DeleteCubeFracturePieces(ObjectId originObjectId) =>
        Execute(DeleteSql, originObjectId, static (command, id) => command.BindInt64("$originObjectId", id.Value));
}
