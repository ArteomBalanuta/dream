namespace Dream.Simulation.Persistence;

public readonly record struct PersistedTerrainChunk(int ChunkX, int ChunkZ, int FormatVersion, int CellCount, byte[] Data);

// Not part of IWorldPersistenceRepository: the admin and export path, not the write queue.
public interface ITerrainChunkRepository
{
    // ORDER BY chunk_z, chunk_x.
    IReadOnlyList<PersistedTerrainChunk> LoadTerrainChunks();

    // One transaction: delete the world's chunks, insert all.
    void ReplaceTerrainChunks(IReadOnlyList<PersistedTerrainChunk> chunks);
}
