using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using Dream.Server.Persistence;
using Dream.Server.Persistence.Repositories;
using Dream.Server.Persistence.Store;
using Dream.Server.Persistence.Worlds;
using Dream.Server.Worlds;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Simulation.World.MapSources;
using Dream.Simulation.World.Terrain;
using Dream.Tests.Server.Persistence.Migrations;
using static Dream.Tests.Integration.ObjectIdTestSupport;
using static Dream.Tests.Integration.PersistentWorldSeedDeterminismIntegrationTests;

namespace Dream.Tests.Integration;

// Persistence wave D, tester adversarial cases on the real SQLite facade (see also
// TerrainEditSqlitePersistenceTests, whose Rig is reused): a lost terrain_cell_overrides row whose
// rubble survived, a corrupt stored chunk, a second destroy of a destroyed cell across restarts,
// and a world copy that is edited independently of its source.
public sealed class WaveDTesterTerrainAdversarialTests(ITestOutputHelper output)
{
    // Crash between the writes, or a deleted row: the rubble rows (origin = the override id)
    // survive but the override does not. Restore heals it: the source cell is removed again, the
    // override row is rewritten under the origin id, so the terrain never returns beneath its own
    // rubble. Ids stay unique and below the counter, and the map stays consistent.
    [Fact]
    public void LostOverrideRow_WithSurvivingRubble_IsHealedOnRestore_NoOverlap_AndTheRowIsRewritten()
    {
        using TempDatabase database = new();
        GeometryCellKey cell;
        int pieces;
        using (ServerPersistenceBundle bundle = database.OpenBundle())
        {
            TerrainEditSqlitePersistenceTests.Rig rig = new(bundle);
            rig.Bridge.RestoreWorld();
            cell = TerrainEditSqlitePersistenceTests.FirstRaisedCell(rig.Host.Map, sculpture: false);
            rig.FractureTerrain(cell);
            pieces = rig.Host.Map.CaptureFracturePieces().Count(piece => piece.Cell == cell);
            Assert.True(pieces > 0);
        }

        SqliteConnection.ClearAllPools();
        PersistedIds beforeLoss = ReadIds(database.DatabasePath);
        long lostId = Assert.Single(beforeLoss.Overrides);
        Execute(database.DatabasePath, "DELETE FROM terrain_cell_overrides;");
        PersistedIds afterLoss = ReadIds(database.DatabasePath);
        Assert.Empty(afterLoss.Overrides);
        // Deleting the row does not lower the counter.
        Assert.Equal(beforeLoss.NextObjectId, afterLoss.NextObjectId);

        using (ServerPersistenceBundle bundle = database.OpenBundle())
        {
            TerrainEditSqlitePersistenceTests.Rig restarted = new(bundle);
            restarted.Bridge.RestoreWorld();
            Map map = restarted.Host.Map;
            int rubble = map.CaptureFracturePieces().Count(piece => piece.Cell == cell);
            output.WriteLine($"lost override {lostId} for {cell}: source cell={map.IsSourceCell(cell)}, rubble pieces restored={rubble}");
            Assert.False(map.IsSourceCell(cell), "the lost override is healed: the terrain cell stays removed");
            Assert.False(map.GeometryCells.ContainsKey(cell));
            Assert.Equal(pieces, rubble);
            Assert.Empty(map.FindInvariantViolations());
            Assert.True(restarted.ObjectIds.Next >= beforeLoss.NextObjectId, "the allocator restarted below the stored counter");
        }

        // The healed row is written on the write queue (flushed when the bundle is disposed).
        SqliteConnection.ClearAllPools();
        PersistedIds healed = ReadIds(database.DatabasePath);
        Assert.Equal(lostId, Assert.Single(healed.Overrides));
        Assert.Equal(beforeLoss.NextObjectId, healed.NextObjectId);
        AssertIdsUnique(healed);

        using (ServerPersistenceBundle bundle = database.OpenBundle())
        {
            TerrainEditSqlitePersistenceTests.Rig third = new(bundle);
            third.Bridge.RestoreWorld();
            Map map = third.Host.Map;
            Assert.False(map.IsSourceCell(cell));
            Assert.Empty(map.FindInvariantViolations());
            Assert.True(third.ObjectIds.Next > healed.MaxId);
        }

        SqliteConnection.ClearAllPools();
        AssertIdsUnique(ReadIds(database.DatabasePath));
    }

    // Overrides are in the same id space as every other kind (the wave C crash tests'
    // AssertNoSharedIdsAcrossKinds is extended with them), and a terrain rubble group's origin is
    // its override id.
    [Fact]
    public void OverrideIds_AreBelowTheCounter_DistinctFromEveryLiveId_AndEqualTheirRubbleOrigins()
    {
        using TempDatabase database = new();
        using (ServerPersistenceBundle bundle = database.OpenBundle())
        {
            TerrainEditSqlitePersistenceTests.Rig rig = new(bundle);
            rig.Bridge.RestoreWorld();
            foreach (GeometryCellKey cell in rig.Host.Map.GeometryCells.Keys
                .Where(key => key.Y > 0 && rig.Host.Map.IsSourceCell(key) && rig.Host.Map.Cubes[key.X, key.Y, key.Z] != null)
                .OrderBy(key => (key.X, key.Z, key.Y)).Take(3).ToList())
            {
                rig.FractureTerrain(cell);
            }
        }

        SqliteConnection.ClearAllPools();
        PersistedIds ids = ReadIds(database.DatabasePath);
        Assert.Equal(3, ids.Overrides.Count);
        AssertIdsUnique(ids);
        Assert.Equal(ids.Overrides.OrderBy(id => id), ids.Origins.OrderBy(id => id));
    }

    [Fact]
    public void TruncatedOrCorruptChunkBlob_InTerrainChunks_FailsLoudlyThroughStoredMapSource()
    {
        MapDefinition definition = new(20, 8, 20, WorldMapSource.Generated, WorldGeneratorCatalog.DefaultKey, WorldGeneratorCatalog.DefaultVersion);
        IReadOnlyList<PersistedTerrainChunk> exported = TerrainChunkExporter.Export(new GeneratedMapSource(definition));
        using MigrationTestDatabase database = new();
        database.Schema.EnsureSchema();
        SqliteTerrainChunkRepository repository = new(new SqliteWorldStore(database.Schema), WorldId.Default);
        repository.ReplaceTerrainChunks(exported);
        SqliteConnection.ClearAllPools();
        _ = new StoredMapSource(definition, repository.LoadTerrainChunks()).CreateCells();

        PersistedTerrainChunk victim = exported[1];
        int length = victim.Data.Length;
        Assert.True(length > 40);
        foreach ((string label, string expression) in new[]
        {
            ("truncated mid-record", $"substr(data, 1, {length - 7})"),
            ("truncated to the minimum", "substr(data, 1, 23)"),
            ("trailing garbage", "unhex(hex(data) || 'FFFFFFFF')"),
            ("first byte flipped", "unhex('FF' || hex(substr(data, 2)))"),
            ("zeroed body", $"unhex(hex(substr(data, 1, 23)) || hex(zeroblob({length - 23})))")
        })
        {
            repository.ReplaceTerrainChunks(exported);
            Execute(database.DatabasePath,
                $"UPDATE terrain_chunks SET data = {expression} WHERE chunk_x = {victim.ChunkX} AND chunk_z = {victim.ChunkZ};");
            IReadOnlyList<PersistedTerrainChunk> loaded = repository.LoadTerrainChunks();
            Exception? failure = Record.Exception(() => new StoredMapSource(definition, loaded).CreateCells());
            output.WriteLine($"{label}: {failure?.GetType().Name}: {failure?.Message}");
            Assert.True(failure != null, $"{label}: a corrupt blob decoded without an error");
            Assert.True(failure is InvalidDataException or InvalidOperationException or EndOfStreamException or ArgumentException or IndexOutOfRangeException,
                $"{label}: unexpected exception type {failure!.GetType().FullName}");
        }
    }

    // A second destroy of an already destroyed cell (a stale record, or the same cell after a
    // restart) writes nothing: one row per cell, the counter does not move, no second rubble group.
    [Fact]
    public void DestroyingTheSameTerrainCellTwice_AcrossRestarts_KeepsOneOverrideRow()
    {
        using TempDatabase database = new();
        GeometryCellKey cell;
        using (ServerPersistenceBundle bundle = database.OpenBundle())
        {
            TerrainEditSqlitePersistenceTests.Rig rig = new(bundle);
            rig.Bridge.RestoreWorld();
            cell = TerrainEditSqlitePersistenceTests.FirstRaisedCell(rig.Host.Map, sculpture: false);
            rig.FractureTerrain(cell);
            // Same session: the second PersistTerrainCellRemoved is ignored.
            Cube other = rig.Host.Map.SolidCubes.First(c => rig.Host.Map.TryGetCellForCube(c, out GeometryCellKey k) && k != cell);
            WorldObjectRecord stale = rig.WorldObjects.RegisterCube(other, rig.Host.Map, maxHealth: 1);
            rig.Bridge.PersistTerrainCellRemoved(stale, cell);
        }

        SqliteConnection.ClearAllPools();
        PersistedIds first = ReadIds(database.DatabasePath);
        Assert.Single(first.Overrides);
        int pieces = first.Pieces.Count;

        using (ServerPersistenceBundle bundle = database.OpenBundle())
        {
            TerrainEditSqlitePersistenceTests.Rig rig = new(bundle);
            rig.Bridge.RestoreWorld();
            Assert.False(rig.Host.Map.IsSourceCell(cell));
            Cube other = rig.Host.Map.SolidCubes.First(c => rig.Host.Map.TryGetCellForCube(c, out GeometryCellKey k) && k != cell);
            WorldObjectRecord stale = rig.WorldObjects.RegisterCube(other, rig.Host.Map, maxHealth: 1);
            // After a restart the destroyed set is rebuilt from the rows.
            rig.Bridge.PersistTerrainCellRemoved(stale, cell);
            // And at the repository: a different id for the same cell is dropped, the counter stays.
            using SqliteWorldPersistenceRepository repository = new(bundle.Schema, bundle.Descriptor.Id);
            repository.SaveTerrainCellOverride(new PersistedTerrainCellOverride(
                new ObjectId(first.NextObjectId + 50), cell, TerrainCellOverrideKind.Removed, null, DateTime.UtcNow, DateTime.UtcNow));
        }

        SqliteConnection.ClearAllPools();
        PersistedIds second = ReadIds(database.DatabasePath);
        Assert.Equal(first.Overrides, second.Overrides);
        Assert.Equal(first.NextObjectId, second.NextObjectId);
        Assert.Equal(pieces, second.Pieces.Count);
        AssertIdsUnique(second);
    }

    [Fact]
    public void DestroyingTerrainInAWorldCopy_DoesNotChangeTheSourceWorld()
    {
        using MigrationTestDatabase database = new();
        SqliteWorldCatalog catalog = WorldBundles.OpenCatalog(database);
        WorldRecord source = catalog.Resolve(WorldSelector.Default, WorldDefaults.StartupFloorSize, out _);
        WorldBundles.Bind(database.Schema, source).Dispose();
        source = catalog.TryGet(WorldId.Default)!;

        GeometryCellKey first;
        using (ServerPersistenceBundle bundle = WorldBundles.Bind(database.Schema, source))
        {
            TerrainEditSqlitePersistenceTests.Rig rig = new(bundle);
            rig.Bridge.RestoreWorld();
            first = TerrainEditSqlitePersistenceTests.FirstRaisedCell(rig.Host.Map, sculpture: false);
            rig.FractureTerrain(first);
        }

        SqliteConnection.ClearAllPools();
        WorldRecord copy = catalog.Copy(source.Descriptor.Id, "twin");
        string overridesBefore = WorldBundles.Dump(database, "terrain_cell_overrides", source.Descriptor.Id);
        string rubbleBefore = WorldBundles.Dump(database, "cube_fracture_pieces", source.Descriptor.Id);
        long counterBefore = Convert.ToInt64(database.Scalar($"SELECT next_object_id FROM worlds WHERE world_id = {source.Descriptor.Id};"));
        Assert.Equal(1, WorldBundles.Count(database, "terrain_cell_overrides", copy.Descriptor.Id));

        GeometryCellKey second;
        using (ServerPersistenceBundle bundle = WorldBundles.Bind(database.Schema, catalog.TryGet(copy.Descriptor.Id)!))
        {
            TerrainEditSqlitePersistenceTests.Rig rig = new(bundle);
            rig.Bridge.RestoreWorld();
            Assert.False(rig.Host.Map.IsSourceCell(first), "the copy keeps the source's destroyed cell");
            second = rig.Host.Map.GeometryCells.Keys
                .Where(key => key.Y > 0 && key != first && rig.Host.Map.IsSourceCell(key) && rig.Host.Map.Cubes[key.X, key.Y, key.Z] != null)
                .OrderBy(key => (key.X, key.Z, key.Y)).First();
            rig.FractureTerrain(second);
            Assert.Empty(rig.Host.Map.FindInvariantViolations());
        }

        SqliteConnection.ClearAllPools();
        Assert.Equal(2, WorldBundles.Count(database, "terrain_cell_overrides", copy.Descriptor.Id));
        Assert.Equal(1, WorldBundles.Count(database, "terrain_cell_overrides", source.Descriptor.Id));
        Assert.Equal(overridesBefore, WorldBundles.Dump(database, "terrain_cell_overrides", source.Descriptor.Id));
        Assert.Equal(rubbleBefore, WorldBundles.Dump(database, "cube_fracture_pieces", source.Descriptor.Id));
        Assert.Equal(counterBefore, Convert.ToInt64(database.Scalar($"SELECT next_object_id FROM worlds WHERE world_id = {source.Descriptor.Id};")));

        using (ServerPersistenceBundle bundle = WorldBundles.Bind(database.Schema, catalog.TryGet(source.Descriptor.Id)!))
        {
            TerrainEditSqlitePersistenceTests.Rig rig = new(bundle);
            rig.Bridge.RestoreWorld();
            Assert.False(rig.Host.Map.IsSourceCell(first));
            Assert.True(rig.Host.Map.IsSourceCell(second), "the copy's edit leaked into the source");
            Assert.NotNull(rig.Host.Map.TryGetCubeAtCell(second.X, second.Y, second.Z));
            Assert.Empty(rig.Host.Map.CaptureFracturePieces().Where(piece => piece.Cell == second));
            Assert.Empty(rig.Host.Map.FindInvariantViolations());
        }
    }

    private static void AssertIdsUnique(PersistedIds ids)
    {
        Assert.Equal(ids.AllObjects.Count, ids.AllObjects.Distinct().Count());
        Assert.Empty(ids.Origins.Intersect(ids.AllObjects));
        Assert.Equal(ids.Overrides.Count, ids.Overrides.Distinct().Count());
        Assert.Empty(ids.Overrides.Intersect(ids.AllObjects));
        Assert.All(ids.AllObjects.Concat(ids.Origins).Concat(ids.Overrides), id => Assert.True(id < ids.NextObjectId, $"id {id} is not below the counter {ids.NextObjectId}"));
    }

    private static void Execute(string databasePath, string sql)
    {
        SqliteConnection.ClearAllPools();
        using SqliteConnection connection = Open(databasePath, readOnly: false);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
