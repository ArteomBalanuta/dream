using Dream.Server.Persistence;
using Dream.Server.Persistence.Repositories;
using Dream.Server.Persistence.Store;
using Dream.Server.Persistence.Worlds;
using Dream.Server.Worlds;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Integration;
using Dream.Tests.Load;
using Dream.Tests.Server.Persistence.Migrations;
using Dream.Tests.Server.Worlds;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Dream.Tests.Server.Persistence.Repositories;

// Persistence wave D (A): the terrain override and chunk repositories, the heal guard, Copy, and the
// world-scoped facade.
public sealed class TerrainRepositoryTests
{
    private static readonly DateTime Created = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    [Fact]
    public void Overrides_RoundTripInObjectIdOrder_AndAdvanceTheCounter()
    {
        using MigrationTestDatabase database = Latest();
        SqliteTerrainOverrideRepository repository = Overrides(database, WorldId.Default);

        repository.SaveTerrainCellOverride(Removed(30, new GeometryCellKey(5, 2, 6)));
        repository.SaveTerrainCellOverride(Removed(20, new GeometryCellKey(1, 1, 1)));
        repository.SaveTerrainCellOverride(new PersistedTerrainCellOverride(
            new ObjectId(25), new GeometryCellKey(2, 3, 2), TerrainCellOverrideKind.Replaced, [1, 2, 3], Created, Created));

        IReadOnlyList<PersistedTerrainCellOverride> loaded = repository.LoadTerrainCellOverrides();
        Assert.Equal([20L, 25L, 30L], loaded.Select(o => o.ObjectId.Value).ToArray());
        Assert.Equal(new GeometryCellKey(1, 1, 1), loaded[0].Cell);
        Assert.Equal(TerrainCellOverrideKind.Removed, loaded[0].Kind);
        Assert.Null(loaded[0].CellData);
        Assert.Equal(Created, loaded[0].CreatedUtc);
        Assert.Equal(TerrainCellOverrideKind.Replaced, loaded[1].Kind);
        Assert.Equal(new byte[] { 1, 2, 3 }, loaded[1].CellData);
        Assert.Equal(31L, database.Scalar("SELECT next_object_id FROM worlds WHERE world_id = 1;"));
    }

    [Fact]
    public void DuplicateCell_IsIgnored_AndLeavesTheCounterAndTheFirstRow()
    {
        using MigrationTestDatabase database = Latest();
        SqliteTerrainOverrideRepository repository = Overrides(database, WorldId.Default);
        GeometryCellKey cell = new(4, 2, 4);
        repository.SaveTerrainCellOverride(Removed(10, cell));
        Assert.Equal(11L, database.Scalar("SELECT next_object_id FROM worlds WHERE world_id = 1;"));

        repository.SaveTerrainCellOverride(Removed(500, cell));

        PersistedTerrainCellOverride only = Assert.Single(repository.LoadTerrainCellOverrides());
        Assert.Equal(10L, only.ObjectId.Value);
        Assert.Equal(11L, database.Scalar("SELECT next_object_id FROM worlds WHERE world_id = 1;"));
    }

    [Fact]
    public void Overrides_AreScopedToTheirWorld()
    {
        using MigrationTestDatabase database = Latest();
        WorldRecord two = new SqliteWorldCatalog(database.Schema).Create(new WorldCreateOptions("two", 20));
        SqliteTerrainOverrideRepository one = Overrides(database, WorldId.Default);
        SqliteTerrainOverrideRepository other = Overrides(database, two.Descriptor.Id);

        one.SaveTerrainCellOverride(Removed(10, new GeometryCellKey(1, 1, 1)));
        other.SaveTerrainCellOverride(Removed(10, new GeometryCellKey(1, 1, 1)));
        other.SaveTerrainCellOverride(Removed(11, new GeometryCellKey(2, 1, 1)));

        Assert.Single(one.LoadTerrainCellOverrides());
        Assert.Equal(2, other.LoadTerrainCellOverrides().Count);
    }

    [Fact]
    public void Overrides_ForwardThroughTheWorldFacade_AndRideAWriteBatch()
    {
        using MigrationTestDatabase database = Latest();
        using SqliteWorldPersistenceRepository facade = new(database.Schema);

        using (SqliteWriteBatch batch = new())
        {
            batch.Activate();
            try
            {
                batch.BeginWrite();
                facade.SaveTerrainCellOverride(Removed(10, new GeometryCellKey(1, 1, 1)));
                facade.SaveTerrainCellOverride(Removed(11, new GeometryCellKey(2, 1, 1)));
                batch.CompleteWrite();
                Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM terrain_cell_overrides;"));
                batch.Commit();
            }
            finally
            {
                SqliteWriteBatch.Deactivate();
            }
        }

        Assert.Equal(2, facade.LoadTerrainCellOverrides().Count);
        Assert.Equal(12L, facade.LoadNextObjectId());
    }

    [Fact]
    public void HealGuard_CoversOverrides()
    {
        using MigrationTestDatabase database = Latest();
        SqliteWorldPersistenceRepository facade = new(database.Schema);
        // A row inserted with the counter put back, as a database edited outside the server.
        database.ExecuteScript("INSERT INTO terrain_cell_overrides (world_id, object_id, cell_x, cell_y, cell_z, kind) VALUES (1, 400, 1, 1, 1, 0); UPDATE worlds SET next_object_id = 3 WHERE world_id = 1;");

        Assert.Equal(401L, facade.LoadNextObjectId());
        Assert.Equal(401L, database.Scalar("SELECT next_object_id FROM worlds WHERE world_id = 1;"));
    }

    [Fact]
    public void Chunks_RoundTripInChunkZThenXOrder()
    {
        using MigrationTestDatabase database = Latest();
        SqliteTerrainChunkRepository repository = Chunks(database, WorldId.Default);
        PersistedTerrainChunk a = Chunk(1, 0, 1);
        PersistedTerrainChunk b = Chunk(0, 1, 2);
        PersistedTerrainChunk c = Chunk(0, 0, 3);

        repository.ReplaceTerrainChunks([a, b, c]);

        IReadOnlyList<PersistedTerrainChunk> loaded = repository.LoadTerrainChunks();
        Assert.Equal([(0, 0), (1, 0), (0, 1)], loaded.Select(x => (x.ChunkX, x.ChunkZ)).ToArray());
        Assert.Equal(c.Data, loaded[0].Data);
        Assert.Equal(1, loaded[0].FormatVersion);
        Assert.Equal(3, loaded[0].CellCount);
    }

    [Fact]
    public void ReplaceChunks_ReplacesOnlyThisWorldsChunks()
    {
        using MigrationTestDatabase database = Latest();
        WorldRecord two = new SqliteWorldCatalog(database.Schema).Create(new WorldCreateOptions("two", 20));
        SqliteTerrainChunkRepository one = Chunks(database, WorldId.Default);
        SqliteTerrainChunkRepository other = Chunks(database, two.Descriptor.Id);
        one.ReplaceTerrainChunks([Chunk(0, 0, 1), Chunk(1, 0, 2)]);
        other.ReplaceTerrainChunks([Chunk(0, 0, 9)]);

        one.ReplaceTerrainChunks([Chunk(3, 3, 4)]);

        PersistedTerrainChunk only = Assert.Single(one.LoadTerrainChunks());
        Assert.Equal((3, 3), (only.ChunkX, only.ChunkZ));
        Assert.Equal(9, Assert.Single(other.LoadTerrainChunks()).CellCount);

        one.ReplaceTerrainChunks([]);
        Assert.Empty(one.LoadTerrainChunks());
        Assert.Single(other.LoadTerrainChunks());
    }

    [Fact]
    public void ReplaceChunks_IsAtomic_AFailedInsertKeepsThePreviousChunks()
    {
        using MigrationTestDatabase database = Latest();
        SqliteTerrainChunkRepository repository = Chunks(database, WorldId.Default);
        repository.ReplaceTerrainChunks([Chunk(0, 0, 1), Chunk(1, 0, 2)]);

        // The second chunk violates length(data) >= 23 after the first was inserted and the old rows deleted.
        PersistedTerrainChunk bad = new(2, 0, 1, 0, new byte[5]);
        Assert.Throws<SqliteException>(() => repository.ReplaceTerrainChunks([Chunk(7, 7, 5), bad]));

        Assert.Equal([(0, 0), (1, 0)], repository.LoadTerrainChunks().Select(x => (x.ChunkX, x.ChunkZ)).ToArray());

        // A duplicate chunk in the input fails the same way.
        Assert.Throws<SqliteException>(() => repository.ReplaceTerrainChunks([Chunk(7, 7, 5), Chunk(7, 7, 6)]));
        Assert.Equal(2, repository.LoadTerrainChunks().Count);
    }

    [Fact]
    public void Bundle_ExposesTheChunkRepository_ForItsWorld()
    {
        using MigrationTestDatabase database = Latest();
        using ServerPersistenceBundle bundle = WorldBundles.Open(database, WorldId.Default);

        Assert.NotNull(bundle.TerrainChunks);
        bundle.TerrainChunks!.ReplaceTerrainChunks([Chunk(0, 0, 1)]);
        Assert.Single(Chunks(database, WorldId.Default).LoadTerrainChunks());
    }

    [Fact]
    public void Copy_PreservesOverridesChunksAndTheCounter()
    {
        using WorldCatalogTestDatabase db = new();
        db.SeedWorldOne();
        db.Execute("""
            INSERT INTO terrain_cell_overrides (world_id, object_id, cell_x, cell_y, cell_z, kind, cell_data)
                VALUES (1, 60, 4, 2, 4, 0, NULL), (1, 61, 5, 3, 5, 1, x'0A0B');
            INSERT INTO terrain_chunks (world_id, chunk_x, chunk_z, format_version, cell_count, data)
                VALUES (1, 0, 0, 1, 2, zeroblob(30)), (1, 1, 0, 1, 0, zeroblob(23));
            UPDATE worlds SET next_object_id = 900 WHERE world_id = 1;
            """);

        WorldRecord copy = db.Catalog.Copy(WorldId.Default, "Copy");
        long id = copy.Descriptor.Id.Value;

        Assert.Equal(
            db.Scalar("SELECT group_concat(object_id || '|' || cell_x || '|' || cell_y || '|' || cell_z || '|' || kind || '|' || COALESCE(hex(cell_data), '-'), ',') FROM (SELECT * FROM terrain_cell_overrides WHERE world_id = 1 ORDER BY object_id)"),
            db.Scalar($"SELECT group_concat(object_id || '|' || cell_x || '|' || cell_y || '|' || cell_z || '|' || kind || '|' || COALESCE(hex(cell_data), '-'), ',') FROM (SELECT * FROM terrain_cell_overrides WHERE world_id = {id} ORDER BY object_id)"));
        Assert.Equal(2L, db.Count("terrain_cell_overrides", id));
        Assert.Equal(
            db.Scalar("SELECT group_concat(chunk_x || '|' || chunk_z || '|' || cell_count || '|' || hex(data), ',') FROM (SELECT * FROM terrain_chunks WHERE world_id = 1 ORDER BY chunk_z, chunk_x)"),
            db.Scalar($"SELECT group_concat(chunk_x || '|' || chunk_z || '|' || cell_count || '|' || hex(data), ',') FROM (SELECT * FROM terrain_chunks WHERE world_id = {id} ORDER BY chunk_z, chunk_x)"));
        Assert.Equal(2L, db.Count("terrain_chunks", id));
        Assert.Equal(900L, db.Scalar($"SELECT next_object_id FROM worlds WHERE world_id = {id}"));
        Assert.Equal(0L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM pragma_foreign_key_check")));
    }

    [Fact]
    public void Delete_RemovesTheWorldsOverridesAndChunks_AndOnlyThose()
    {
        using WorldCatalogTestDatabase db = new();
        db.SeedWorldOne();
        db.Execute("""
            INSERT INTO terrain_cell_overrides (world_id, object_id, cell_x, cell_y, cell_z, kind) VALUES (1, 60, 4, 2, 4, 0);
            INSERT INTO terrain_chunks (world_id, chunk_x, chunk_z, format_version, cell_count, data) VALUES (1, 0, 0, 1, 0, zeroblob(23));
            """);
        WorldRecord copy = db.Catalog.Copy(WorldId.Default, "Copy");
        Assert.Equal(1L, db.Count("terrain_cell_overrides", copy.Descriptor.Id.Value));

        Assert.True(db.Catalog.Delete(copy.Descriptor.Id));

        Assert.Equal(0L, db.Count("terrain_cell_overrides", copy.Descriptor.Id.Value));
        Assert.Equal(0L, db.Count("terrain_chunks", copy.Descriptor.Id.Value));
        Assert.Equal(1L, db.Count("terrain_cell_overrides", 1));
        Assert.Equal(1L, db.Count("terrain_chunks", 1));
        Assert.Equal(0L, Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM pragma_foreign_key_check")));
    }

    [Fact]
    public void SeedTerrainOverrides_WritesTheFirstRaisedCells_WithIdsFromTheCounter_AndNoRubble()
    {
        using MigrationTestDatabase database = Latest();
        database.ExecuteScript("UPDATE worlds SET next_object_id = 1000 WHERE world_id = 1;");

        IReadOnlyList<GeometryCellKey> cells = WorldSeeder.SeedTerrainOverrides(database.DatabasePath, WorldDescriptor.Default, 25);

        Assert.Equal(25, cells.Count);
        Assert.All(cells, c => Assert.True(c.Y > 0));
        Assert.Equal(cells.Distinct().Count(), cells.Count);
        IReadOnlyList<PersistedTerrainCellOverride> loaded = Overrides(database, WorldId.Default).LoadTerrainCellOverrides();
        Assert.Equal(Enumerable.Range(1000, 25).Select(i => (long)i).ToArray(), loaded.Select(o => o.ObjectId.Value).ToArray());
        Assert.Equal(cells, loaded.Select(o => o.Cell).ToArray());
        Assert.All(loaded, o => Assert.Equal(TerrainCellOverrideKind.Removed, o.Kind));
        Assert.Equal(1025L, database.Scalar("SELECT next_object_id FROM worlds WHERE world_id = 1;"));
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM cube_fracture_pieces;"));
    }

    [Fact]
    public void DumpWorld_IncludesBothTerrainTables_InOrder()
    {
        using MigrationTestDatabase database = Latest();
        Overrides(database, WorldId.Default).SaveTerrainCellOverride(Removed(12, new GeometryCellKey(2, 1, 2)));
        Overrides(database, WorldId.Default).SaveTerrainCellOverride(Removed(11, new GeometryCellKey(1, 1, 1)));
        Chunks(database, WorldId.Default).ReplaceTerrainChunks([Chunk(1, 0, 2), Chunk(0, 0, 1)]);
        MigrationTestDatabase.ReleasePooledConnections();

        string dump = ObjectIdTestSupport.DumpWorld(database.DatabasePath);

        Assert.Contains("terrain_cell_overrides: 2 rows\n  11 | 1 | 1 | 1 | 0 | null".ReplaceLineEndings(), dump.ReplaceLineEndings(), StringComparison.Ordinal);
        Assert.Contains("terrain_chunks: 2 rows", dump, StringComparison.Ordinal);
        Assert.True(dump.IndexOf("  11 |", StringComparison.Ordinal) < dump.IndexOf("  12 |", StringComparison.Ordinal));
    }

    private static MigrationTestDatabase Latest()
    {
        MigrationTestDatabase database = new();
        database.Schema.EnsureSchema();
        return database;
    }

    private static SqliteTerrainOverrideRepository Overrides(MigrationTestDatabase database, WorldId world) =>
        new(new SqliteWorldStore(database.Schema), world);

    private static SqliteTerrainChunkRepository Chunks(MigrationTestDatabase database, WorldId world) =>
        new(new SqliteWorldStore(database.Schema), world);

    private static PersistedTerrainCellOverride Removed(long id, GeometryCellKey cell) =>
        new(new ObjectId(id), cell, TerrainCellOverrideKind.Removed, CellData: null, Created, Created);

    private static PersistedTerrainChunk Chunk(int x, int z, int cellCount) =>
        new(x, z, 1, cellCount, Enumerable.Range(0, 30).Select(i => (byte)(i + x + z * 7 + cellCount)).ToArray());
}
