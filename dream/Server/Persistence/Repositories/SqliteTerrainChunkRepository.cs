using Dream.Server.Persistence.Store;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Microsoft.Data.Sqlite;

namespace Dream.Server.Persistence.Repositories;

// terrain_chunks: the stored map of a world as versioned chunk blobs. The admin and export path, not
// the write queue: ReplaceTerrainChunks always runs in its own owned transaction (never enlisted in a
// write batch), so a failed insert leaves the previous chunks untouched.
public sealed class SqliteTerrainChunkRepository(IWorldStore store, WorldId worldId)
    : SqliteRepositoryBase(store, worldId), ITerrainChunkRepository
{
    private const string LoadSql = """
        SELECT chunk_x, chunk_z, format_version, cell_count, data
        FROM terrain_chunks
        WHERE world_id = $worldId
        ORDER BY chunk_z, chunk_x;
        """;

    private const string DeleteSql = "DELETE FROM terrain_chunks WHERE world_id = $worldId;";

    private const string InsertSql = """
        INSERT INTO terrain_chunks (world_id, chunk_x, chunk_z, format_version, cell_count, data, updated_utc)
        VALUES ($worldId, $chunkX, $chunkZ, $formatVersion, $cellCount, $data, $updatedUtc);
        """;

    public IReadOnlyList<PersistedTerrainChunk> LoadTerrainChunks() =>
        QueryList(LoadSql, static reader => new PersistedTerrainChunk(
            ChunkX: reader.GetInt32(0),
            ChunkZ: reader.GetInt32(1),
            FormatVersion: reader.GetInt32(2),
            CellCount: reader.GetInt32(3),
            Data: (byte[])reader.GetValue(4)));

    public void ReplaceTerrainChunks(IReadOnlyList<PersistedTerrainChunk> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        long world = WorldIdValue;
        string writeUtc = SqliteSchemaBootstrapper.FormatUtc(DateTime.UtcNow);

        using SqliteSession session = Store.BeginOwnedTransaction();
        using (SqliteCommand delete = session.CreateCommand(DeleteSql))
        {
            delete.Parameters.AddWithValue("$worldId", world);
            delete.ExecuteNonQuery();
        }

        using (SqliteCommand insert = session.CreateCommand(InsertSql))
        {
            insert.Parameters.AddWithValue("$worldId", world);
            insert.Parameters.AddWithValue("$updatedUtc", writeUtc);
            SqliteParameter data = insert.AddParameter("$data", SqliteType.Blob);
            foreach (PersistedTerrainChunk chunk in chunks)
            {
                insert.BindInt64("$chunkX", chunk.ChunkX);
                insert.BindInt64("$chunkZ", chunk.ChunkZ);
                insert.BindInt64("$formatVersion", chunk.FormatVersion);
                insert.BindInt64("$cellCount", chunk.CellCount);
                data.Value = chunk.Data;
                insert.ExecuteNonQuery();
            }
        }

        session.Commit();
    }
}
