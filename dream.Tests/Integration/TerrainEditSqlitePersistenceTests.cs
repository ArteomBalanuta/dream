using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Sessions;
using Dream.Server.Persistence;
using Dream.Simulation.Enemies;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Simulation.World.MapSources;
using static Dream.Tests.Integration.ObjectIdTestSupport;
using static Dream.Tests.Integration.PersistentWorldSeedDeterminismIntegrationTests;

namespace Dream.Tests.Integration;

// Persistence wave D glue: workstream B's terrain-edit persistence (TerrainEditPersistenceTests uses
// in-memory fakes) against the real SQLite facade (migration 0006, SqliteTerrainOverrideRepository,
// the object-id triggers and DeleteCubeFracturePieces). The bridge runs with an inline write queue,
// so every write is committed when the call returns; a "restart" is a new bundle and bridge on the
// same database file.
public sealed class TerrainEditSqlitePersistenceTests
{
    private const string Builder = "builder";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TerrainFracture_IsStoredInSqlite_AndARestartKeepsTheCellDestroyed_WithItsOwnerlessRubble(bool sculpture)
    {
        using TempDatabase database = new();
        GeometryCellKey cell;
        int livePieces;
        ulong liveChecksum;
        using (ServerPersistenceBundle bundle = database.OpenBundle())
        {
            Rig rig = new(bundle);
            rig.Bridge.RestoreWorld();
            cell = FirstRaisedCell(rig.Host.Map, sculpture);
            rig.FractureTerrain(cell);
            livePieces = rig.Host.Map.CaptureFracturePieces().Count(piece => piece.Cell == cell);
            liveChecksum = rig.Host.Map.WorldChecksum;
            Assert.True(livePieces > 0);
        }

        SqliteConnection.ClearAllPools();
        long overrideId = Scalar(database.DatabasePath,
            $"SELECT object_id FROM terrain_cell_overrides WHERE world_id = 1 AND cell_x = {cell.X} AND cell_y = {cell.Y} AND cell_z = {cell.Z} AND kind = 0 AND cell_data IS NULL;");
        Assert.Equal(1L, Scalar(database.DatabasePath, "SELECT COUNT(*) FROM terrain_cell_overrides;"));
        Assert.Equal(livePieces, Scalar(database.DatabasePath,
            $"SELECT COUNT(*) FROM cube_fracture_pieces WHERE origin_object_id = {overrideId} AND owner_player_name IS NULL AND parent_cell_x = {cell.X} AND parent_cell_y = {cell.Y} AND parent_cell_z = {cell.Z};"));
        Assert.Equal(livePieces, Scalar(database.DatabasePath, "SELECT COUNT(*) FROM cube_fracture_pieces;"));
        PersistedIds ids = ReadIds(database.DatabasePath);
        Assert.DoesNotContain(overrideId, ids.AllObjects);
        Assert.True(ids.NextObjectId > Math.Max(overrideId, ids.MaxId), "the counter is not above every id");

        using (ServerPersistenceBundle bundle = database.OpenBundle())
        {
            Rig restarted = new(bundle);
            restarted.Bridge.RestoreWorld();
            Map map = restarted.Host.Map;
            Assert.False(map.IsSourceCell(cell));
            Assert.False(map.GeometryCells.ContainsKey(cell));
            Assert.Null(map.TryGetCubeAtCell(cell.X, cell.Y, cell.Z));
            Assert.Equal(livePieces, map.CaptureFracturePieces().Count(piece => piece.Cell == cell));
            Assert.Empty(map.FindInvariantViolations());
            Assert.Equal(liveChecksum, map.WorldChecksum);
            Assert.True(restarted.ObjectIds.Next >= ids.NextObjectId);
        }

        // The restore rewrote nothing.
        SqliteConnection.ClearAllPools();
        PersistedIds afterRestart = ReadIds(database.DatabasePath);
        Assert.Equal(ids.Pieces, afterRestart.Pieces);
        Assert.Equal(ids.Origins, afterRestart.Origins);
        Assert.Equal(ids.NextObjectId, afterRestart.NextObjectId);
        Assert.Equal(1L, Scalar(database.DatabasePath, "SELECT COUNT(*) FROM terrain_cell_overrides;"));
    }

    // Wave C's orphan repro on SQLite: a cube placed over terrain rubble and broken deletes the old
    // group's rows (DeleteCubeFracturePieces on the real repository); the override stays.
    [Fact]
    public void CubePlacedOverTerrainRubbleThenBroken_DeletesTheOldGroupFromSqlite()
    {
        using TempDatabase database = new();
        GeometryCellKey cell;
        using (ServerPersistenceBundle bundle = database.OpenBundle())
        {
            bundle.PlayerProfiles.GetOrCreate(Builder);
            Rig rig = new(bundle);
            rig.Bridge.RestoreWorld();
            Map map = rig.Host.Map;
            cell = FirstRaisedCell(map, sculpture: false);
            rig.FractureTerrain(cell);

            Cube placed = map.TryAddCube(new Vector3(cell.X, cell.Y, cell.Z))!;
            Assert.NotNull(placed);
            WorldObjectRecord record = rig.WorldObjects.RegisterCube(placed, map, maxHealth: 1, ownerPlayerName: Builder);
            rig.Bridge.PersistPlacedCube(record, GeometryMaterialId.Grass);
            Assert.True(map.FractureCubeIntoPieces(cell));
            rig.Bridge.DeletePersistedPlacedCube(record.Actor.Id);
            rig.Bridge.PersistFracturePieces(record, cell);
        }

        SqliteConnection.ClearAllPools();
        long overrideId = Scalar(database.DatabasePath, "SELECT object_id FROM terrain_cell_overrides;");
        PersistedIds ids = ReadIds(database.DatabasePath);
        Assert.Empty(ids.Cubes);
        long origin = Assert.Single(ids.Origins);
        Assert.NotEqual(overrideId, origin);
        Assert.Equal(0L, Scalar(database.DatabasePath, $"SELECT COUNT(*) FROM cube_fracture_pieces WHERE origin_object_id = {overrideId};"));
        Assert.Equal((long)ids.Pieces.Count, Scalar(database.DatabasePath, $"SELECT COUNT(*) FROM cube_fracture_pieces WHERE owner_player_name = '{Builder}';"));

        List<(string Category, string Message)> lines = [];
        using (ServerPersistenceBundle bundle = database.OpenBundle())
        using (NetworkDiagnosticsLog.CaptureOnCurrentThread((category, message) => lines.Add((category, message))))
        {
            Rig restarted = new(bundle);
            restarted.Bridge.RestoreWorld();
            Assert.Equal(ids.Pieces.Count, restarted.Host.Map.CaptureFracturePieces().Count(piece => piece.Cell == cell));
            Assert.False(restarted.Host.Map.IsSourceCell(cell));
        }

        Assert.DoesNotContain(lines, line => line.Category == "server.persistence.orphan-rubble-pruned");
    }

    // Rows orphaned before wave D: two groups for one cell in the database. The restart keeps the
    // newest, deletes the other through the real repository, and logs the prune.
    [Fact]
    public void OrphanRubbleGroups_ArePrunedFromSqlite_OnTheFirstRestart()
    {
        using TempDatabase database = new();
        GeometryCellKey cell;
        using (ServerPersistenceBundle bundle = database.OpenBundle())
        {
            bundle.PlayerProfiles.GetOrCreate(Builder);
            Map map = new(new GeneratedMapSource(MapDefinition.ForWorld(bundle.Descriptor)), physics: null, headless: true);
            cell = FirstEmptyCellAboveTerrain(map);
            long next = bundle.World.LoadNextObjectId();
            DateTime now = DateTime.UtcNow;
            Vector3 parent = new(cell.X, cell.Y, cell.Z);
            // Older group: origin next, pieces next+1, next+2. Newer: origin next+3, piece next+4.
            foreach ((long id, long origin, int index, float x) in new[]
            {
                (next + 1, next, 0, 0.25f), (next + 2, next, 1, 0.75f), (next + 4, next + 3, 0, 0.5f)
            })
            {
                bundle.World.SaveCubeFracturePiece(new PersistedCubeFracturePiece(
                    new ObjectId(id), new ObjectId(origin), index, Builder, parent,
                    new Vector3(cell.X + x, cell.Y + 0.25f, cell.Z + 0.25f), 0.4f, GeometryMaterialId.Grass, now, now));
            }
        }

        SqliteConnection.ClearAllPools();
        PersistedIds before = ReadIds(database.DatabasePath);
        Assert.Equal(2, before.Origins.Count);
        long newest = before.Origins.Max();

        List<(string Category, string Message)> lines = [];
        using (ServerPersistenceBundle bundle = database.OpenBundle())
        using (NetworkDiagnosticsLog.CaptureOnCurrentThread((category, message) => lines.Add((category, message))))
        {
            Rig rig = new(bundle);
            rig.Bridge.RestoreWorld();
            (GeometryCellKey Cell, int PieceIndex, GeometryFracturePiece Piece, GeometryMaterialId MaterialId) restored =
                Assert.Single(rig.Host.Map.CaptureFracturePieces());
            Assert.Equal(cell, restored.Cell);
            // Pruned ids stay reserved.
            Assert.True(rig.ObjectIds.Next > before.MaxId);
        }

        (string _, string message) = Assert.Single(lines, line => line.Category == "server.persistence.orphan-rubble-pruned");
        Assert.Contains("count=1", message, StringComparison.Ordinal);
        SqliteConnection.ClearAllPools();
        PersistedIds after = ReadIds(database.DatabasePath);
        Assert.Equal([newest], after.Origins);
        Assert.Single(after.Pieces);
        Assert.Equal(before.NextObjectId, after.NextObjectId);
    }

    internal static GeometryCellKey FirstRaisedCell(Map map, bool sculpture)
    {
        return map.GeometryCells
            .Where(pair => pair.Key.Y > 0
                && (pair.Value.SubdivisionState != null) == sculpture
                && map.IsSourceCell(pair.Key)
                && map.Cubes[pair.Key.X, pair.Key.Y, pair.Key.Z] != null)
            .Select(pair => pair.Key)
            .OrderBy(key => (key.X, key.Z, key.Y))
            .First();
    }

    private static GeometryCellKey FirstEmptyCellAboveTerrain(Map map)
    {
        for (int x = 2; x < map.Width - 2; x++)
        {
            for (int z = 2; z < map.Width - 2; z++)
            {
                GeometryCellKey key = new(x, 2, z);
                if (!map.GeometryCells.ContainsKey(key) && !map.GeometryCells.ContainsKey(new GeometryCellKey(x, 1, z)))
                {
                    return key;
                }
            }
        }

        throw new InvalidOperationException("no empty cell above the floor");
    }

    // RuntimePersistenceBridge over the bundle's real facade and profile repository, with an inline
    // write queue and a map built from the bundle's world.
    internal sealed class Rig
    {
        public Rig(ServerPersistenceBundle bundle)
        {
            Host = new FakeHost(new Map(new GeneratedMapSource(MapDefinition.ForWorld(bundle.Descriptor)), physics: null, headless: true));
            Bridge = new RuntimePersistenceBridge(
                Host,
                bundle.PlayerProfiles,
                bundle.World,
                new NullCheckpointWriter(),
                PersistenceWriteQueue.Inline(),
                WorldObjects,
                new EnemyRegistry(),
                new SceneEventHub(),
                ObjectIds,
                new ObjectIdentityMap());
            ObjectIds.Restore(bundle.World.LoadNextObjectId());
        }

        public FakeHost Host { get; }

        public ObjectIdAllocator ObjectIds { get; } = new();

        public WorldObjectRegistry WorldObjects { get; } = new();

        public RuntimePersistenceBridge Bridge { get; }

        // The resolver's fracture branch for a base-terrain cube (no ObjectId, no owner).
        public void FractureTerrain(GeometryCellKey cell)
        {
            Cube cube = Host.Map.Cubes[cell.X, cell.Y, cell.Z]!;
            WorldObjectRecord record = WorldObjects.RegisterCube(cube, Host.Map, maxHealth: 1);
            Assert.True(Host.Map.IsSourceCell(cell));
            Assert.True(Host.Map.FractureCubeIntoPieces(cell));
            Bridge.DeletePersistedPlacedCube(record.Actor.Id);
            Bridge.PersistTerrainCellRemoved(record, cell);
            Bridge.PersistFracturePieces(record, cell);
            WorldObjects.Unregister(cube, preserveDetachedRecord: false);
        }
    }

    internal sealed class NullCheckpointWriter : IPersistenceCheckpointWriter
    {
        public void Write(PersistenceCheckpoint checkpoint)
        {
        }
    }

    internal sealed class FakeHost(Map map) : IRuntimePersistenceHost
    {
        public Map Map { get; } = map;

        public PlayerSessionRegistry Sessions { get; } = new();

        public bool IsDisposed => false;

        public PlayerInteractionMode GetSessionInteractionMode(PlayerSessionId sessionId) => PlayerInteractionMode.Building;

        public bool TryGetPlayerCheckpointState(
            PlayerSessionId sessionId,
            out Vector3 position,
            out Vector3 facing,
            out int currentHealth,
            out int maxHealth)
        {
            position = facing = default;
            currentHealth = maxHealth = 0;
            return false;
        }

        public bool TryAllocateEnemyActorId(out int actorId)
        {
            actorId = 3000;
            return true;
        }

        public void SpawnInitialWolf()
        {
        }

        public bool IsPlayerInventoryLoaded(string playerName) => false;

        public void EvictPlayerInventory(string playerName)
        {
        }
    }
}
