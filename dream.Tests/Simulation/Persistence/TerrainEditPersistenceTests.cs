using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Rules;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Simulation.World.MapSources;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Simulation.Persistence;

// Persistence wave D, workstream B: a destroyed base-terrain cell is persisted as a Removed override
// whose ObjectId is the origin of its rubble, replayed on restart through Map.RemoveSourceCell, and a
// cell fractured twice no longer leaves orphan rubble rows. The bridge runs against an in-memory
// repository that behaves like the SQL one (ids, deletes), so a "restart" is a second rig on it.
public sealed class TerrainEditPersistenceTests
{
    private const int Size = 56;
    private const string Builder = "builder";

    private static MapDefinition Definition { get; } =
        new(Size, 8, Size, WorldMapSource.Generated, WorldGeneratorCatalog.DefaultKey, WorldGeneratorCatalog.DefaultVersion);

    private static GeometryCellKey FirstRaisedCell(bool sculpture)
    {
        return new GeneratedMapSource(Definition).CreateCells()
            .First(record => record.Key.Y > 0 && (record.SubdivisionState != null) == sculpture)
            .Key;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TerrainFracture_WritesOneOverride_AndPiecesWhoseOriginIsTheOverride(bool sculpture)
    {
        FakeWorld world = new();
        Rig rig = new(world);
        GeometryCellKey cell = FirstRaisedCell(sculpture);

        rig.FractureTerrain(cell);

        PersistedTerrainCellOverride row = Assert.Single(world.Overrides);
        Assert.Equal((cell, TerrainCellOverrideKind.Removed), (row.Cell, row.Kind));
        Assert.Null(row.CellData);
        Assert.NotEmpty(world.Pieces);
        Assert.All(world.Pieces, piece =>
        {
            Assert.Equal(row.ObjectId, piece.OriginObjectId);
            Assert.Null(piece.OwnerPlayerName);
        });
        Assert.Equal(world.Pieces.Count + 1, AllIds(world).Distinct().Count());
    }

    [Fact]
    public void TerrainFracture_OfTheSameCellTwice_WritesTheOverrideOnce()
    {
        FakeWorld world = new();
        Rig rig = new(world);
        GeometryCellKey cell = FirstRaisedCell(false);
        rig.FractureTerrain(cell);

        WorldObjectRecord record = new WorldObjectRegistry().RegisterCube(
            rig.Host.Map.TryAddCube(new Vector3(cell.X, cell.Y, cell.Z))!, rig.Host.Map, maxHealth: 1);
        rig.Bridge.PersistTerrainCellRemoved(record, cell);

        Assert.Single(world.Overrides);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Restart_KeepsTheTerrainCellDestroyed_RestoresItsRubble_AndReservesUniqueIds(bool sculpture)
    {
        FakeWorld world = new();
        Rig first = new(world);
        GeometryCellKey cell = FirstRaisedCell(sculpture);
        first.FractureTerrain(cell);
        int pieceCount = world.Pieces.Count;
        long highest = AllIds(world).Max(id => id.Value);

        Rig second = new(world);
        second.Bridge.RestoreWorld();

        Map map = second.Host.Map;
        Assert.False(map.IsSourceCell(cell));
        Assert.False(map.GeometryCells.ContainsKey(cell));
        Assert.Null(map.Cubes[cell.X, cell.Y, cell.Z]);
        Assert.Equal(pieceCount, map.CaptureFracturePieces().Count(piece => piece.Cell == cell));
        Assert.Empty(map.FindInvariantViolations());
        Assert.Equal(WorldStateChecksum.ComputeFull(map), map.WorldChecksum);
        // The allocator is past every id of every kind, and the next ones collide with none.
        Assert.Equal(highest + 1, second.ObjectIds.Next);
        HashSet<ObjectId> existing = [.. AllIds(world)];
        Assert.DoesNotContain(second.ObjectIds.Take(), existing);

        // The restored world equals the one that fractured live, cell for cell.
        Assert.Equal(first.Host.Map.WorldChecksum, map.WorldChecksum);
        Assert.Equal(first.Host.Map.GeometryCells.Keys.ToHashSet(), map.GeometryCells.Keys.ToHashSet());
        // Nothing was rewritten by the restore.
        Assert.Single(world.Overrides);
        Assert.Equal(pieceCount, world.Pieces.Count);
    }

    [Fact]
    public void ACubePlacedInARemovedTerrainCell_PersistsAndRestores()
    {
        FakeWorld world = new();
        Rig first = new(world, knownPlayers: [Builder]);
        GeometryCellKey cell = FirstRaisedCell(false);
        first.FractureTerrain(cell);
        first.Bridge.RestoreWorld();
        Cube placed = first.Host.Map.TryAddCube(new Vector3(cell.X, cell.Y, cell.Z))!;
        WorldObjectRecord record = first.WorldObjects.RegisterCube(placed, first.Host.Map, ownerPlayerName: Builder);
        first.Bridge.PersistPlacedCube(record, GeometryMaterialId.Grass);

        PersistedPlacedCube row = Assert.Single(world.Cubes);
        Assert.Equal(new Vector3(cell.X, cell.Y, cell.Z), row.CellPosition);

        Rig second = new(world, knownPlayers: [Builder]);
        second.Bridge.RestoreWorld();

        WorldObjectRecord restored = Assert.Single(second.WorldObjects.Records);
        Assert.Equal(Builder, restored.OwnerPlayerName);
        Assert.True(second.Identities.TryGetObjectId(restored.Actor.Id, out ObjectId restoredId));
        Assert.Equal(row.ObjectId, restoredId);
        Assert.NotNull(second.Host.Map.Cubes[cell.X, cell.Y, cell.Z]);
        Assert.Empty(second.Host.Map.FindInvariantViolations());
        Assert.Equal(highestPlusOne(world), second.ObjectIds.Next);

        static long highestPlusOne(FakeWorld w) => AllIds(w).Max(id => id.Value) + 1;
    }

    // Wave C's repro: a cube placed over terrain rubble and then broken replaced the rubble group in
    // the map but left the first group's rows in the database.
    [Fact]
    public void CubePlacedOverTerrainRubbleThenBroken_LeavesNoOrphanRubbleRows()
    {
        FakeWorld world = new();
        Rig rig = new(world, knownPlayers: [Builder]);
        GeometryCellKey cell = FirstRaisedCell(false);
        rig.FractureTerrain(cell);
        ObjectId overrideId = Assert.Single(world.Overrides).ObjectId;

        Cube placed = rig.Host.Map.TryAddCube(new Vector3(cell.X, cell.Y, cell.Z))!;
        WorldObjectRecord record = rig.WorldObjects.RegisterCube(placed, rig.Host.Map, maxHealth: 1, ownerPlayerName: Builder);
        rig.Bridge.PersistPlacedCube(record, GeometryMaterialId.Grass);
        ObjectId cubeId = Assert.Single(world.Cubes).ObjectId;
        Assert.True(rig.Host.Map.FractureCubeIntoPieces(cell));
        rig.Bridge.DeletePersistedPlacedCube(record.Actor.Id);
        rig.Bridge.PersistFracturePieces(record, cell);

        Assert.Empty(world.Cubes);
        Assert.Equal(overrideId, Assert.Single(world.Overrides).ObjectId);
        // Only the new group's rows remain, one per piece the map holds for the cell.
        Assert.NotEmpty(world.Pieces);
        Assert.All(world.Pieces, piece => Assert.Equal(cubeId, piece.OriginObjectId));
        Assert.Equal(rig.Host.Map.CaptureFracturePieces().Count(piece => piece.Cell == cell), world.Pieces.Count);

        List<(string Category, string Message)> lines = [];
        Rig restarted = new(world);
        using (NetworkDiagnosticsLog.CaptureOnCurrentThread((category, message) => lines.Add((category, message))))
        {
            restarted.Bridge.RestoreWorld();
        }

        Assert.DoesNotContain(lines, line => line.Category == "server.persistence.orphan-rubble-pruned");
        Assert.Equal(world.Pieces.Count, restarted.Host.Map.CaptureFracturePieces().Count(piece => piece.Cell == cell));
    }

    [Fact]
    public void SecondFractureOfACell_TombstonesTheReplacedGroupInTheNextIncrementalCheckpoint()
    {
        FakeWorld world = new();
        Rig rig = new(world, knownPlayers: [Builder]);
        GeometryCellKey cell = FirstRaisedCell(false);
        rig.FractureTerrain(cell);
        ObjectId overrideId = Assert.Single(world.Overrides).ObjectId;
        Assert.False(rig.Bridge.CapturePersistenceCheckpoint(full: true)!.IsIncremental);

        Cube placed = rig.Host.Map.TryAddCube(new Vector3(cell.X, cell.Y, cell.Z))!;
        WorldObjectRecord record = rig.WorldObjects.RegisterCube(placed, rig.Host.Map, maxHealth: 1, ownerPlayerName: Builder);
        rig.Bridge.PersistPlacedCube(record, GeometryMaterialId.Grass);
        Assert.True(rig.Host.Map.FractureCubeIntoPieces(cell));
        rig.Bridge.DeletePersistedPlacedCube(record.Actor.Id);
        rig.Bridge.PersistFracturePieces(record, cell);

        PersistenceCheckpoint incremental = rig.Bridge.CapturePersistenceCheckpoint(full: false)!;

        Assert.True(incremental.IsIncremental);
        Assert.Contains(overrideId, incremental.RemovedFractureOriginObjectIds);
        Assert.DoesNotContain(incremental.FracturePieces, piece => piece.OriginObjectId == overrideId);
        Assert.Null(rig.Bridge.CapturePersistenceCheckpoint(full: false));
    }

    [Fact]
    public void RestoredOverrideWhoseCellIsNotASourceCell_IsSkippedAndLogged_AndItsRowAndIdAreKept()
    {
        FakeWorld world = new();
        PersistedTerrainCellOverride outOfBounds = new(
            new ObjectId(5), new GeometryCellKey(5000, 3, 5000), TerrainCellOverrideKind.Removed, null, Now, Now);
        PersistedTerrainCellOverride replaced = new(
            new ObjectId(6), new GeometryCellKey(2, 3, 2), TerrainCellOverrideKind.Replaced, [1], Now, Now);
        world.Overrides.Add(outOfBounds);
        world.Overrides.Add(replaced);
        world.NextObjectId = 7;
        Rig rig = new(world);
        List<(string Category, string Message)> lines = [];

        using (NetworkDiagnosticsLog.CaptureOnCurrentThread((category, message) => lines.Add((category, message))))
        {
            rig.Bridge.RestoreWorld();
        }

        (string _, string skipped) = Assert.Single(lines.Where(line => line.Category == "server.persistence.terrain-override-skipped"));
        Assert.Contains("count=1", skipped);
        Assert.Contains("reason=not-a-source-cell", skipped);
        Assert.Single(lines.Where(line => line.Category == "server.persistence.terrain-override-unsupported"));
        Assert.Equal([outOfBounds, replaced], world.Overrides);
        Assert.Equal(7, rig.ObjectIds.Next);
        Assert.Empty(rig.Host.Map.FindInvariantViolations());
    }

    [Fact]
    public void SeveralRubbleGroupsForOneCell_KeepTheOneWithTheNewestPieces_AndPruneTheRest_Logged()
    {
        FakeWorld world = new();
        GeometryCellKey cell = new(4, 2, 4);
        foreach ((long origin, long firstPiece, float x) in new[] { (3L, 10L, 4.25f), (9L, 20L, 4.75f), (5L, 30L, 4.5f) })
        {
            world.Pieces.Add(new PersistedCubeFracturePiece(
                new ObjectId(firstPiece), new ObjectId(origin), 0, null, new Vector3(cell.X, cell.Y, cell.Z),
                new Vector3(x, 2.25f, 4.25f), 0.4f, GeometryMaterialId.Grass, Now, Now));
        }

        world.NextObjectId = 40;
        Rig rig = new(world);
        List<(string Category, string Message)> lines = [];

        using (NetworkDiagnosticsLog.CaptureOnCurrentThread((category, message) => lines.Add((category, message))))
        {
            rig.Bridge.RestoreWorld();
        }

        (string _, string message) = Assert.Single(lines.Where(line => line.Category == "server.persistence.orphan-rubble-pruned"));
        Assert.Contains("count=2", message);
        PersistedCubeFracturePiece kept = Assert.Single(world.Pieces);
        // Newest = highest piece id (30), not highest origin (9).
        Assert.Equal(new ObjectId(5), kept.OriginObjectId);
        (GeometryCellKey Cell, int PieceIndex, GeometryFracturePiece Piece, GeometryMaterialId MaterialId) restored =
            Assert.Single(rig.Host.Map.CaptureFracturePieces());
        Assert.Equal(new Vector3(4.5f, 2.25f, 4.25f), restored.Piece.Center);
        // Pruned groups' ids stay reserved: they were issued.
        Assert.Equal(40, rig.ObjectIds.Next);
    }

    // A placed cube's origin is its placement id and physics can move it to another cell: an older
    // cube broken later has a lower origin but newer pieces, and its group is the live one.
    [Fact]
    public void RubbleGroupWithLowerOriginButNewerPieces_IsKept_AndTheStaleHigherOriginGroupPruned()
    {
        FakeWorld world = new();
        GeometryCellKey cell = new(4, 2, 4);
        foreach ((long origin, long piece, float x) in new[] { (9L, 20L, 4.75f), (2L, 30L, 4.5f) })
        {
            world.Pieces.Add(new PersistedCubeFracturePiece(
                new ObjectId(piece), new ObjectId(origin), 0, null, new Vector3(cell.X, cell.Y, cell.Z),
                new Vector3(x, 2.25f, 4.25f), 0.4f, GeometryMaterialId.Grass, Now, Now));
        }

        world.NextObjectId = 40;
        Rig rig = new(world);

        rig.Bridge.RestoreWorld();

        PersistedCubeFracturePiece kept = Assert.Single(world.Pieces);
        Assert.Equal(new ObjectId(2), kept.OriginObjectId);
        Assert.Equal(new Vector3(4.5f, 2.25f, 4.25f), Assert.Single(rig.Host.Map.CaptureFracturePieces()).Piece.Center);
    }

    [Fact]
    public void FractureWithNoPersistableOrigin_StillDeletesTheReplacedGroup()
    {
        FakeWorld world = new();
        Rig rig = new(world);
        GeometryCellKey cell = FirstRaisedCell(false);
        rig.FractureTerrain(cell);
        ObjectId overrideId = Assert.Single(world.Overrides).ObjectId;
        Assert.NotEmpty(world.Pieces);
        Assert.False(rig.Bridge.CapturePersistenceCheckpoint(full: true)!.IsIncremental);

        // An unowned cube that was never persisted: no origin, so PersistFracturePieces returns early.
        Cube placed = rig.Host.Map.TryAddCube(new Vector3(cell.X, cell.Y, cell.Z))!;
        WorldObjectRecord record = rig.WorldObjects.RegisterCube(placed, rig.Host.Map, maxHealth: 1);
        Assert.True(rig.Host.Map.FractureCubeIntoPieces(cell));
        rig.Bridge.PersistFracturePieces(record, cell);

        Assert.Empty(world.Pieces);
        Assert.Contains(overrideId, rig.Bridge.CapturePersistenceCheckpoint(full: false)!.RemovedFractureOriginObjectIds);
    }

    [Fact]
    public void IncrementalCheckpointAfterATerrainFracture_HoldsOnlyPieceRows()
    {
        FakeWorld world = new();
        Rig rig = new(world);
        rig.Bridge.RestoreWorld();
        Assert.False(rig.Bridge.CapturePersistenceCheckpoint(full: false)!.IsIncremental);
        GeometryCellKey cell = FirstRaisedCell(false);

        rig.FractureTerrain(cell);
        PersistenceCheckpoint incremental = rig.Bridge.CapturePersistenceCheckpoint(full: false)!;

        Assert.True(incremental.IsIncremental);
        Assert.Equal(world.Pieces.Count, incremental.FracturePieces.Count);
        Assert.All(incremental.FracturePieces, piece => Assert.Equal(Assert.Single(world.Overrides).ObjectId, piece.OriginObjectId));
        Assert.Empty(incremental.PlacedCubes);
        Assert.Empty(incremental.Enemies);
        Assert.Empty(incremental.RemovedCubeObjectIds);
        Assert.Empty(incremental.RemovedFractureOriginObjectIds);
        // The override is written once, on the queue: the full checkpoint after it holds pieces only too.
        PersistenceCheckpoint full = rig.Bridge.CapturePersistenceCheckpoint(full: true)!;
        Assert.Equal(world.Pieces.Count, full.FracturePieces.Count);
        Assert.Empty(full.PlacedCubes);
    }

    // The bridge finds the terrain override's id as "the retired cube" for the rubble persist that
    // follows it, so the resolver's order (delete, terrain-removed, fracture-persist) is load-bearing.
    [Fact]
    public void Resolver_CallsDeleteThenTerrainRemovedThenFracturePersist_AndTheRubbleOriginIsTheOverride()
    {
        FakeWorld world = new();
        Rig rig = new(world);
        GeometryCellKey cell = FirstRaisedCell(sculpture: false);
        Map map = rig.Host.Map;
        Cube cube = map.Cubes[cell.X, cell.Y, cell.Z]!;
        WorldObjectRecord record = rig.WorldObjects.RegisterCube(cube, map, maxHealth: 1);
        OrderRecordingHost host = new(rig.Bridge);
        AuthoritativeCombatResolver resolver = new(
            1UL, () => map, rig.Enemies, rig.WorldObjects, new SceneEventHub(), CombatFormulaService.Default, host);

        AuthoritativeActionResult result = resolver.ProcessCombatUse(
            new PlayerActionMessage(new PlayerSessionId("s"), 1, new CombatUseIntent(Vector3.Zero, Vector3.UnitX)),
            new CombatUseIntent(new Vector3(cell.X + 0.5f, cell.Y + 4f, cell.Z + 0.5f), -Vector3.UnitY),
            PlayerInteractionMode.Combat,
            new PlayerCombatant(() => Vector3.Zero),
            tick: 9UL);

        Assert.True(Assert.IsType<CombatResolutionResult>(result.Payload).TargetDied);
        Assert.Equal(
            ["RepairCubeRecord", "DeletePersistedPlacedCube", "PersistTerrainCellRemoved", "PersistFracturePieces"],
            host.Calls);
        PersistedTerrainCellOverride row = Assert.Single(world.Overrides);
        Assert.Equal(cell, row.Cell);
        Assert.NotEmpty(world.Pieces);
        Assert.All(world.Pieces, piece => Assert.Equal(row.ObjectId, piece.OriginObjectId));
    }

    private static PersistedCubeFracturePiece Rubble(long origin, long id, GeometryCellKey cell) =>
        new(new ObjectId(id), new ObjectId(origin), 0, null, new Vector3(cell.X, cell.Y, cell.Z),
            new Vector3(cell.X + 0.25f, cell.Y + 0.25f, cell.Z + 0.25f), 0.4f, GeometryMaterialId.Grass, Now, Now);

    private static PersistedPlacedCube PlacedCube(long id, Vector3 cell) =>
        new(new ObjectId(id), Builder, cell, GeometryShapeKind.SolidCube, GeometryMaterialId.Grass, 1, 1, Now, Now);

    private static List<(string Category, string Message)> Restore(Rig rig)
    {
        List<(string Category, string Message)> lines = [];
        using (NetworkDiagnosticsLog.CaptureOnCurrentThread((category, message) => lines.Add((category, message))))
        {
            rig.Bridge.RestoreWorld();
        }

        return lines;
    }

    [Fact]
    public void LostOverrideRow_WithSurvivingRubble_IsHealed_Written_AndLogged()
    {
        FakeWorld world = new();
        GeometryCellKey cell = FirstRaisedCell(false);
        world.Pieces.Add(Rubble(50, 51, cell));
        world.NextObjectId = 60;
        Rig rig = new(world);
        Assert.True(rig.Host.Map.IsSourceCell(cell));

        List<(string Category, string Message)> lines = Restore(rig);

        (string _, string message) = Assert.Single(lines.Where(line => line.Category == "server.persistence.terrain-override-healed"));
        Assert.Equal("count=1", message);
        PersistedTerrainCellOverride row = Assert.Single(world.Overrides);
        Assert.Equal((new ObjectId(50), cell, TerrainCellOverrideKind.Removed), (row.ObjectId, row.Cell, row.Kind));
        Assert.False(rig.Host.Map.IsSourceCell(cell));
        Assert.Single(rig.Host.Map.CaptureFracturePieces(), piece => piece.Cell == cell);
        Assert.Empty(rig.Host.Map.FindInvariantViolations());
    }

    [Fact]
    public void KeptRubbleOfADeletedPlayerCube_OverANonSourceCell_IsNotHealed()
    {
        FakeWorld world = new();
        GeometryCellKey cell = new(3, 7, 3); // above the generated terrain: not a source cell
        world.Pieces.Add(Rubble(50, 51, cell));
        world.NextObjectId = 60;
        Rig rig = new(world);
        Assert.False(rig.Host.Map.IsSourceCell(cell));

        List<(string Category, string Message)> lines = Restore(rig);

        Assert.Empty(world.Overrides);
        Assert.DoesNotContain(lines, line => line.Category == "server.persistence.terrain-override-healed");
        Assert.Single(rig.Host.Map.CaptureFracturePieces(), piece => piece.Cell == cell);
    }

    [Fact]
    public void KeptRubbleWhoseOriginIsAPersistedCube_IsNotHealed()
    {
        FakeWorld world = new();
        GeometryCellKey cell = FirstRaisedCell(false);
        world.Pieces.Add(Rubble(50, 51, cell));
        world.Cubes.Add(PlacedCube(50, new Vector3(3, 7, 3)));
        world.NextObjectId = 60;
        Rig rig = new(world, knownPlayers: [Builder]);

        List<(string Category, string Message)> lines = Restore(rig);

        Assert.Empty(world.Overrides);
        Assert.True(rig.Host.Map.IsSourceCell(cell));
        Assert.DoesNotContain(lines, line => line.Category == "server.persistence.terrain-override-healed");
    }

    [Fact]
    public void ReplacedOverrideRow_WithRubbleInItsCell_IsNeverHealed()
    {
        FakeWorld world = new();
        GeometryCellKey cell = FirstRaisedCell(false);
        PersistedTerrainCellOverride replaced = new(new ObjectId(6), cell, TerrainCellOverrideKind.Replaced, [1], Now, Now);
        world.Overrides.Add(replaced);
        world.Pieces.Add(Rubble(50, 51, cell));
        world.NextObjectId = 60;
        Rig rig = new(world);

        List<(string Category, string Message)> lines = Restore(rig);

        Assert.Equal([replaced], world.Overrides);
        Assert.True(rig.Host.Map.IsSourceCell(cell));
        Assert.DoesNotContain(lines, line => line.Category == "server.persistence.terrain-override-healed");
    }

    // The heal runs before placed cubes are restored, so a persisted cube in a healed cell returns on the
    // same restart instead of being skipped as "cell occupied".
    [Fact]
    public void PersistedCubeInACellWhoseOverrideWasLost_RestoresOnTheSameRestartAsTheHeal()
    {
        FakeWorld world = new();
        GeometryCellKey cell = FirstRaisedCell(false);
        world.Pieces.Add(Rubble(50, 51, cell));
        world.Cubes.Add(PlacedCube(70, new Vector3(cell.X, cell.Y, cell.Z)));
        world.NextObjectId = 80;
        Rig rig = new(world, knownPlayers: [Builder]);

        List<(string Category, string Message)> lines = Restore(rig);

        Assert.Single(lines.Where(line => line.Category == "server.persistence.terrain-override-healed"));
        Assert.DoesNotContain(lines, line => line.Category == "server.persistence.restore-skipped-cubes");
        Assert.Equal(new ObjectId(50), Assert.Single(world.Overrides).ObjectId);
        WorldObjectRecord restored = Assert.Single(rig.WorldObjects.Records);
        Assert.Equal(Builder, restored.OwnerPlayerName);
        Assert.NotNull(rig.Host.Map.Cubes[cell.X, cell.Y, cell.Z]);
        Assert.Single(rig.Host.Map.CaptureFracturePieces(), piece => piece.Cell == cell);
        Assert.Empty(rig.Host.Map.FindInvariantViolations());
    }

    private static readonly DateTime Now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    private static IEnumerable<ObjectId> AllIds(FakeWorld world)
    {
        return world.Overrides.Select(row => row.ObjectId)
            .Concat(world.Pieces.Select(piece => piece.ObjectId))
            .Concat(world.Cubes.Select(cube => cube.ObjectId));
    }

    private sealed class Rig
    {
        public Rig(FakeWorld world, string[]? knownPlayers = null)
        {
            Host = new FakeHost(new Map(new GeneratedMapSource(Definition), physics: null, headless: true));
            ObjectIds = new ObjectIdAllocator();
            Identities = new ObjectIdentityMap();
            FakeProfiles? profiles = knownPlayers == null ? null : new FakeProfiles(knownPlayers);
            Bridge = new RuntimePersistenceBridge(
                Host,
                profiles,
                world,
                new FakeCheckpointWriter(),
                PersistenceWriteQueue.Inline(),
                WorldObjects,
                Enemies,
                new SceneEventHub(),
                ObjectIds,
                Identities);
            // A fresh runtime without a restore still allocates after the database's counter.
            ObjectIds.Restore(world.LoadNextObjectId());
        }

        public FakeHost Host { get; }

        public ObjectIdAllocator ObjectIds { get; }

        public ObjectIdentityMap Identities { get; }

        public WorldObjectRegistry WorldObjects { get; } = new();

        public EnemyRegistry Enemies { get; } = new();

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

    // The runtime's combat host over a real bridge, recording the order of the persistence calls.
    private sealed class OrderRecordingHost(RuntimePersistenceBridge bridge) : IAuthoritativeCombatHost
    {
        public List<string> Calls { get; } = [];

        public bool TryGetSessionCombatant(PlayerSessionId sessionId, out PlayerCombatant? combatant)
        {
            combatant = null;
            return false;
        }

        public void PersistPlayerProgression(PlayerSessionId sessionId, PlayerCombatant combatant)
        {
        }

        public void DeletePersistedEnemy(int enemyId)
        {
        }

        public void GrantLoot(PlayerSessionId killer, string enemyKind, int enemyActorId, ulong tick)
        {
        }

        public WorldObjectRecord RepairCubeRecord(WorldObjectRecord record)
        {
            Calls.Add("RepairCubeRecord");
            return bridge.RepairCubeRecordFromPersistence(record);
        }

        public void UpdatePersistedCubeHealth(WorldObjectRecord record) => bridge.UpdatePersistedCubeHealth(record);

        public void DeletePersistedPlacedCube(int cubeId)
        {
            Calls.Add("DeletePersistedPlacedCube");
            bridge.DeletePersistedPlacedCube(cubeId);
        }

        public void PersistFracturePieces(WorldObjectRecord record, GeometryCellKey cell)
        {
            Calls.Add("PersistFracturePieces");
            bridge.PersistFracturePieces(record, cell);
        }

        public void PersistTerrainCellRemoved(WorldObjectRecord record, GeometryCellKey cell)
        {
            Calls.Add("PersistTerrainCellRemoved");
            bridge.PersistTerrainCellRemoved(record, cell);
        }
    }

    private sealed class FakeHost(Map map) : IRuntimePersistenceHost
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

    private sealed class FakeProfiles(string[] names) : IPlayerProfileRepository
    {
        public PersistedPlayerProfile GetOrCreate(string playerName) => PersistedPlayerProfile.CreateDefault(playerName, Now);

        public PersistedPlayerProfile? TryGet(string playerName) =>
            names.Contains(playerName, StringComparer.OrdinalIgnoreCase)
                ? PersistedPlayerProfile.CreateDefault(playerName, Now)
                : null;

        public void Save(PersistedPlayerProfile profile)
        {
        }
    }

    private sealed class FakeCheckpointWriter : IPersistenceCheckpointWriter
    {
        public void Write(PersistenceCheckpoint checkpoint)
        {
        }
    }

    // In-memory world that mirrors the SQL repository's id counter, inserts and deletes.
    private sealed class FakeWorld : IWorldPersistenceRepository
    {
        public long NextObjectId { get; set; } = 1;

        public List<PersistedPlacedCube> Cubes { get; } = [];

        public List<PersistedCubeFracturePiece> Pieces { get; } = [];

        public List<PersistedTerrainCellOverride> Overrides { get; } = [];

        private void Advance(ObjectId id) => NextObjectId = Math.Max(NextObjectId, id.Value + 1);

        public long LoadNextObjectId() => NextObjectId;

        public IReadOnlyList<PersistedPlacedCube> LoadPlacedCubes() => [.. Cubes];

        public void SavePlacedCube(PersistedPlacedCube cube)
        {
            Cubes.RemoveAll(existing => existing.ObjectId == cube.ObjectId);
            Cubes.Add(cube);
            Advance(cube.ObjectId);
        }

        public void DeletePlacedCube(ObjectId objectId) => Cubes.RemoveAll(cube => cube.ObjectId == objectId);

        public IReadOnlyList<PersistedCubeFracturePiece> LoadCubeFracturePieces() => [.. Pieces];

        public void SaveCubeFracturePiece(PersistedCubeFracturePiece piece)
        {
            Pieces.RemoveAll(existing => existing.ObjectId == piece.ObjectId);
            Pieces.Add(piece);
            Advance(piece.ObjectId);
        }

        public void DeleteCubeFracturePieces(ObjectId originObjectId) =>
            Pieces.RemoveAll(piece => piece.OriginObjectId == originObjectId);

        public IReadOnlyList<PersistedTerrainCellOverride> LoadTerrainCellOverrides() =>
            [.. Overrides.OrderBy(row => row.ObjectId)];

        public void SaveTerrainCellOverride(PersistedTerrainCellOverride cellOverride)
        {
            if (Overrides.Any(existing => existing.Cell == cellOverride.Cell))
            {
                return;
            }

            Overrides.Add(cellOverride);
            Advance(cellOverride.ObjectId);
        }

        public PersistedPlayerWorldState? TryGetPlayerWorldState(string playerName) => null;

        public void SavePlayerWorldState(PersistedPlayerWorldState state)
        {
        }

        public IReadOnlyList<PersistedEnemyInstance> LoadActiveEnemies() => [];

        public void SaveEnemyInstance(PersistedEnemyInstance enemy)
        {
        }

        public void DeleteEnemyInstance(ObjectId objectId)
        {
        }

        public IReadOnlyList<PersistedItemInstance> LoadItemInstances() => [];

        public void SaveItemInstance(PersistedItemInstance item)
        {
        }

        public void DeleteItemInstance(long itemInstanceId)
        {
        }

        public IReadOnlyList<PersistedInventorySlot> LoadInventorySlots() => [];

        public PersistedPlayerInventory LoadPlayerInventory(string playerName) => PersistedPlayerInventory.Empty;

        public void SaveInventorySlot(PersistedInventorySlot slot)
        {
        }

        public void DeleteInventorySlots(string playerName)
        {
        }

        public void DeleteInventorySlot(string playerName, int slotIndex, long expectedItemInstanceId)
        {
        }

        public void DestroyInventoryItem(string playerName, long itemInstanceId)
        {
        }

        public void SaveStarterKitGrant(string playerName, int kitVersion)
        {
        }
    }
}
