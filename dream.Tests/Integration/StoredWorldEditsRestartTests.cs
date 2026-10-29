using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Enemies;
using Dream.Simulation.Persistence;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Simulation.World.MapSources;
using Dream.Simulation.World.Terrain;
using static Dream.Tests.Integration.ObjectIdTestSupport;
using static Dream.Tests.Integration.PersistentWorldSeedDeterminismIntegrationTests;

namespace Dream.Tests.Integration;

// Persistence wave E, workstream D: edits on a stored world behave exactly as on a generated one.
// The runtime is built on the C0 plumbing (WorldSessionRuntime storedTerrain, through
// RuntimePersistencePorts.StoredTerrain), so these run without the server bootstrap hosting stored
// worlds. Stored base = the exported default (56x8x56) terrain, so each stored map equals the
// generated one cell for cell.
public sealed class StoredWorldEditsRestartTests(ITestOutputHelper output)
{
    private static readonly int[] PhaseTicks = [600, 200, 200];

    // A terrain fracture on a stored runtime writes one override and N rubble pieces; a restart
    // keeps the cell absent, restores the rubble with unique ids, and a cube placed in the removed
    // cell comes back.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TerrainFracture_OnAStoredRuntime_WritesOneOverrideAndPieces_AndARestartRestoresThem(bool sculpture)
    {
        using StoredWorldDb db = new();
        GeometryCellKey cell;
        int pieces;
        ulong liveChecksum;
        using (StoredRig rig = new(db))
        {
            Assert.True(rig.Host.Map.Source is StoredMapSource);
            Assert.Equal(WorldMapSource.Stored, rig.Descriptor.MapSource);
            cell = TerrainEditSqlitePersistenceTests.FirstRaisedCell(rig.Host.Map, sculpture);
            rig.FractureTerrain(cell);
            pieces = rig.Host.Map.CaptureFracturePieces().Count(piece => piece.Cell == cell);
            liveChecksum = rig.Host.Map.WorldChecksum;
            Assert.True(pieces > 0);
        }

        SqliteConnection.ClearAllPools();
        Assert.Equal(1L, Scalar(db.DatabasePath, "SELECT COUNT(*) FROM terrain_cell_overrides;"));
        long overrideId = Scalar(db.DatabasePath,
            $"SELECT object_id FROM terrain_cell_overrides WHERE cell_x = {cell.X} AND cell_y = {cell.Y} AND cell_z = {cell.Z} AND kind = 0 AND cell_data IS NULL;");
        Assert.Equal(pieces, Scalar(db.DatabasePath, $"SELECT COUNT(*) FROM cube_fracture_pieces WHERE origin_object_id = {overrideId};"));
        // The stored chunks are the base: an edit never rewrites them.
        Assert.Equal(db.BaseChunks.Count, (int)Scalar(db.DatabasePath, "SELECT COUNT(*) FROM terrain_chunks;"));
        PersistedIds ids = ReadIds(db.DatabasePath);
        Assert.True(ids.NextObjectId > ids.MaxId);

        int placedPieces;
        using (StoredRig rig = new(db))
        {
            Map map = rig.Host.Map;
            Assert.False(map.IsSourceCell(cell));
            Assert.False(map.GeometryCells.ContainsKey(cell));
            Assert.Null(map.TryGetCubeAtCell(cell.X, cell.Y, cell.Z));
            Assert.Equal(pieces, map.CaptureFracturePieces().Count(piece => piece.Cell == cell));
            Assert.Equal(liveChecksum, map.WorldChecksum);
            Assert.Empty(map.FindInvariantViolations());
            Assert.True(rig.ObjectIds.Next >= ids.NextObjectId);

            // A cube placed in the removed cell, over its own rubble.
            Cube placed = map.TryAddCube(new Vector3(cell.X, cell.Y, cell.Z))!;
            Assert.NotNull(placed);
            WorldObjectRecord record = rig.WorldObjects.RegisterCube(placed, map, maxHealth: 10, ownerPlayerName: "builder");
            rig.Bridge.PersistPlacedCube(record, GeometryMaterialId.Grass);
            placedPieces = pieces;
        }

        SqliteConnection.ClearAllPools();
        PersistedIds afterPlace = ReadIds(db.DatabasePath);
        Assert.Single(afterPlace.Cubes);
        Assert.Equal(1L, Scalar(db.DatabasePath, "SELECT COUNT(*) FROM terrain_cell_overrides;"));

        using (StoredRig rig = new(db))
        {
            Map map = rig.Host.Map;
            Assert.False(map.IsSourceCell(cell));
            Assert.NotNull(map.TryGetCubeAtCell(cell.X, cell.Y, cell.Z));
            Assert.Equal(placedPieces, map.CaptureFracturePieces().Count(piece => piece.Cell == cell));
            Assert.Empty(map.FindInvariantViolations());
        }

        SqliteConnection.ClearAllPools();
        PersistedIds afterRestart = ReadIds(db.DatabasePath);
        Assert.Equal(afterPlace.Cubes, afterRestart.Cubes);
        Assert.Equal(afterPlace.Pieces, afterRestart.Pieces);
        Assert.Equal(afterPlace.NextObjectId, afterRestart.NextObjectId);
        Assert.Equal(afterRestart.AllObjects.Count, afterRestart.AllObjects.Distinct().Count());
        output.WriteLine($"{(sculpture ? "sculpture" : "cube")} cell {cell}: 1 override, {pieces} pieces, restored, placed cube restored");
    }

    // A cell broken, a cube placed in it and broken again (the wave D replacement path) on a stored
    // world: one group per cell and unique ids after a restart.
    [Fact]
    public void CubePlacedInARemovedCell_ThenBroken_LeavesOneRubbleGroup_AfterRestart()
    {
        using StoredWorldDb db = new();
        GeometryCellKey cell;
        using (StoredRig rig = new(db))
        {
            Map map = rig.Host.Map;
            cell = TerrainEditSqlitePersistenceTests.FirstRaisedCell(map, sculpture: false);
            rig.FractureTerrain(cell);
            Cube placed = map.TryAddCube(new Vector3(cell.X, cell.Y, cell.Z))!;
            Assert.NotNull(placed);
            WorldObjectRecord record = rig.WorldObjects.RegisterCube(placed, map, maxHealth: 1, ownerPlayerName: "builder");
            rig.Bridge.PersistPlacedCube(record, GeometryMaterialId.Grass);
            Assert.True(map.FractureCubeIntoPieces(cell));
            rig.Bridge.DeletePersistedPlacedCube(record.Actor.Id);
            rig.Bridge.PersistFracturePieces(record, cell);
        }

        using (StoredRig rig = new(db))
        {
            SqliteConnection.ClearAllPools();
            string summary = StoredWorldChecks.AssertConsistent(db, rig.Host.Map);
            Assert.Single(rig.Host.Map.CaptureFracturePieces().Select(piece => piece.Cell).Distinct());
            output.WriteLine(summary);
        }
    }

    // The scripted session of TerrainLockstepRestartTests, on a stored world: two servers, two
    // restarts. Checksums are equal on every tick of every phase and the database dumps (overrides
    // and chunks included) are equal after every phase.
    [Fact]
    public void TwoStoredServers_WithTerrainFractures_RestartingTwice_KeepEqualChecksumsEveryTick_AndEqualDatabases()
    {
        using StoredWorldDb left = new();
        // Seed the world row's metadata once (a first open), then copy the file.
        using (left.CreateHost())
        {
        }

        using StoredWorldDb right = left.Copy();
        long previousOverrides = 0;
        for (int phase = 0; phase < PhaseTicks.Length; phase++)
        {
            int divergence = RunPhase(left, right, PhaseTicks[phase], out int compared, out int rubbleCells);
            Assert.True(divergence < 0, $"phase {phase + 1}: the servers diverged at tick {divergence}");
            SqliteConnection.ClearAllPools();
            string leftDump = DumpWorld(left.DatabasePath);
            Assert.Equal(leftDump, DumpWorld(right.DatabasePath));
            PersistedIds ids = ReadIds(left.DatabasePath);
            Assert.Equal(ids.AllObjects.Count, ids.AllObjects.Distinct().Count());
            Assert.Empty(ids.Origins.Intersect(ids.AllObjects));
            long overrides = Scalar(left.DatabasePath, "SELECT COUNT(*) FROM terrain_cell_overrides WHERE world_id = 1;");
            Assert.True(overrides > 0, $"phase {phase + 1}: no terrain override was stored");
            Assert.True(overrides >= previousOverrides);
            previousOverrides = overrides;
            Assert.Equal(left.BaseChunks.Count, (int)Scalar(left.DatabasePath, "SELECT COUNT(*) FROM terrain_chunks WHERE world_id = 1;"));
            output.WriteLine($"phase {phase + 1}: {compared} ticks equal; rubble cells {rubbleCells}; overrides {overrides}; dumps equal ({leftDump.Length} chars)");
        }

        // After the last phase, both restored maps equal the base minus the stored removals.
        using StoredRig first = new(left);
        using StoredRig second = new(right);
        Assert.Equal(first.Host.Map.WorldChecksum, second.Host.Map.WorldChecksum);
        SqliteConnection.ClearAllPools();
        StoredWorldChecks.AssertConsistent(left, first.Host.Map);
    }

    private static int RunPhase(StoredWorldDb left, StoredWorldDb right, int ticks, out int compared, out int rubbleCells)
    {
        int firstDivergence = -1;
        compared = 0;
        (ServerPersistenceBundle leftBundle, IReadOnlyList<PersistedTerrainChunk>? leftTerrain) = left.OpenBundle();
        (ServerPersistenceBundle rightBundle, IReadOnlyList<PersistedTerrainChunk>? rightTerrain) = right.OpenBundle();
        using PersistentScriptedServer first = new(StoredWorldDb.CreateHost(leftBundle, leftTerrain));
        using PersistentScriptedServer second = new(StoredWorldDb.CreateHost(rightBundle, rightTerrain));
        Assert.IsType<StoredMapSource>(first.Runtime.Map.Source);
        Assert.IsType<StoredMapSource>(second.Runtime.Map.Source);
        Assert.Equal(first.Seed, second.Seed);
        Assert.Equal(first.Hash(), second.Hash());
        Assert.Equal(first.Runtime.Map.WorldChecksum, second.Runtime.Map.WorldChecksum);
        Assert.Equal(first.Runtime.ObjectIds.Next, second.Runtime.ObjectIds.Next);
        for (int tick = 1; tick <= ticks; tick++)
        {
            ScriptStep step = Script(tick, first);
            first.Advance(tick, step);
            second.Advance(tick, step);
            compared++;
            if (firstDivergence < 0 &&
                (first.Hash() != second.Hash() || first.Runtime.Map.WorldChecksum != second.Runtime.Map.WorldChecksum))
            {
                firstDivergence = tick;
            }
        }

        Assert.Equal(first.Runtime.ObjectIds.Next, second.Runtime.ObjectIds.Next);
        Assert.Equal(
            first.Runtime.Map.RubbleCells.Keys.OrderBy(key => (key.X, key.Y, key.Z)).ToArray(),
            second.Runtime.Map.RubbleCells.Keys.OrderBy(key => (key.X, key.Y, key.Z)).ToArray());
        rubbleCells = first.Runtime.Map.RubbleCells.Count;
        return firstDivergence;
    }

    // RuntimePersistenceBridge over the real SQLite facade and a Map built from the stored chunks,
    // with an inline write queue (TerrainEditSqlitePersistenceTests.Rig, but on a StoredMapSource).
    // A "restart" is a new rig on the same database file.
    internal sealed class StoredRig : IDisposable
    {
        private readonly ServerPersistenceBundle _bundle;

        public StoredRig(StoredWorldDb db)
        {
            (_bundle, IReadOnlyList<PersistedTerrainChunk>? terrain) = db.OpenBundle();
            Descriptor = _bundle.Descriptor;
            Assert.NotNull(terrain);
            Host = new TerrainEditSqlitePersistenceTests.FakeHost(
                new Map(new StoredMapSource(MapDefinition.ForWorld(Descriptor), terrain), physics: null, headless: true));
            Bridge = new RuntimePersistenceBridge(
                Host,
                _bundle.PlayerProfiles,
                _bundle.World,
                new TerrainEditSqlitePersistenceTests.NullCheckpointWriter(),
                PersistenceWriteQueue.Inline(),
                WorldObjects,
                new EnemyRegistry(),
                new SceneEventHub(),
                ObjectIds,
                new ObjectIdentityMap());
            _bundle.PlayerProfiles.GetOrCreate("builder");
            ObjectIds.Restore(_bundle.World.LoadNextObjectId());
            Bridge.RestoreWorld();
        }

        public WorldDescriptor Descriptor { get; }

        public TerrainEditSqlitePersistenceTests.FakeHost Host { get; }

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

        public void Dispose()
        {
            _bundle.Dispose();
        }
    }
}
