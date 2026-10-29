using Dream.Server.Persistence.Store;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Microsoft.Data.Sqlite;

namespace Dream.Server.Persistence.Repositories;

// The checkpoint write of cube_fracture_pieces, run by SqlitePersistenceCheckpointWriter inside
// its owned transaction.
public sealed partial class SqliteRubbleRepository
{
    // Update only: the write may land after the queue deleted the rubble, which must stay deleted.
    internal const string CheckpointUpdateSql = """
        UPDATE cube_fracture_pieces SET
            center_x = $centerX,
            center_y = $centerY,
            center_z = $centerZ,
            side_length = $sideLength,
            updated_utc = $updatedUtc
        WHERE world_id = $worldId AND object_id = $objectId;
        """;

    // One prepared command for all rows, its parameters set per row. writeUtc is the formatted
    // write time every row is stamped with.
    internal static void CheckpointUpdate(SqliteSession session, WorldId worldId, IReadOnlyList<PersistedCubeFracturePiece> pieces, string writeUtc)
    {
        if (pieces.Count == 0)
        {
            return;
        }

        using SqliteCommand command = session.CreateCommand(SqliteRepositorySql.WithCheckpointLiterals(CheckpointUpdateSql, worldId, writeUtc));
        SqliteParameter objectId = command.AddParameter("$objectId", SqliteType.Integer);
        SqliteParameter centerX = command.AddParameter("$centerX", SqliteType.Real);
        SqliteParameter centerY = command.AddParameter("$centerY", SqliteType.Real);
        SqliteParameter centerZ = command.AddParameter("$centerZ", SqliteType.Real);
        SqliteParameter sideLength = command.AddParameter("$sideLength", SqliteType.Real);

        foreach (PersistedCubeFracturePiece piece in pieces)
        {
            objectId.Value = piece.ObjectId.Value;
            centerX.Value = piece.Center.X;
            centerY.Value = piece.Center.Y;
            centerZ.Value = piece.Center.Z;
            sideLength.Value = piece.SideLength;
            command.ExecuteNonQuery();
        }
    }
}
