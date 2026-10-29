using Xunit;
using Xunit.Abstractions;
using Dream.Server.Persistence.Repositories;
using Dream.Server.Persistence.Store;
using Dream.Server.Worlds;
using Dream.Simulation.Persistence;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Simulation.World.MapSources;
using Dream.Simulation.World.Terrain;

namespace Dream.Tests.Server.Worlds;

// Persistence wave E (C): `--worlds reset-terrain <id|name> --yes` restores a world's own base terrain.
public sealed class WorldResetTerrainTests(ITestOutputHelper output)
{
    private const string HealedCategory = "server.persistence.terrain-override-healed";

    private const string ForeignRubble =
        "INSERT INTO cube_fracture_pieces (world_id, object_id, origin_object_id, piece_index, owner_player_name, parent_cell_x, parent_cell_y, parent_cell_z, center_x, center_y, center_z, side_length, material_id) " +
        "VALUES (1, 900001, 900000, 0, NULL, 5, 1, 5, 5.1, 1.1, 5.1, 0.5, 'stone');";

    [Fact]
    public void OverridesAndRubbleOfRestoredCellsAreGone_OtherRubbleStays()
    {
        using TerrainCliFixture f = new();
        List<GeometryCellKey> removed = f.FractureTerrain(flat: 3, sculpture: 1);
        // Rubble of a live placed cube's origin in a cell it stands in is not lost-override rubble: it stays.
        f.Exec(ForeignRubble);
        f.Exec("INSERT INTO placed_cube_instances (world_id, object_id, owner_player_name, cell_x, cell_y, cell_z, shape_kind, material_id, current_health, max_health) VALUES (1, 900000, NULL, 5, 1, 5, 'SolidCube', 'stone', 10, 10);");
        long total = f.Scalar("SELECT COUNT(*) FROM cube_fracture_pieces WHERE world_id = 1;");

        (int exit, string line, string error) = f.Cli("reset-terrain", "1", "--yes");
        Assert.Equal(0, exit);
        Assert.Contains("stop the server hosting this world first", error);
        string[] fields = line.TrimEnd().Split('\t');
        Assert.Equal(["reset-terrain", "1", "default"], fields[..3]);
        Assert.Equal("cleared=4", fields[3]);
        Assert.Equal($"rubble={total - 1}", fields[4]);
        Assert.Equal("kept=0", fields[5]);

        Assert.Equal(0, f.Scalar("SELECT COUNT(*) FROM terrain_cell_overrides WHERE world_id = 1;"));
        Assert.Equal(1, f.Scalar("SELECT COUNT(*) FROM cube_fracture_pieces WHERE world_id = 1;"));
        Assert.Equal(1, f.Scalar("SELECT COUNT(*) FROM cube_fracture_pieces WHERE object_id = 900001;"));

        // Again: nothing left to do.
        Assert.StartsWith("reset-terrain\t1\tdefault\tcleared=0\trubble=0\tkept=0", f.Cli("reset-terrain", "default", "--yes").Out);
        Assert.Equal(4, removed.Count);
    }

    [Fact]
    public void CellsUnderAPlacedCube_AreKeptWithTheirRubble_AndReported()
    {
        using TerrainCliFixture f = new();
        List<GeometryCellKey> removed = f.FractureTerrain(flat: 3);
        GeometryCellKey underCube = removed[0];
        long keptRubble;
        using (TerrainRig rig = f.OpenRig(WorldId.Default))
        {
            rig.Restore();
            rig.PlaceCube(underCube);
        }

        keptRubble = f.Scalar(
            $"SELECT COUNT(*) FROM cube_fracture_pieces WHERE world_id = 1 AND parent_cell_x = {underCube.X} AND parent_cell_y = {underCube.Y} AND parent_cell_z = {underCube.Z};");
        Assert.True(keptRubble > 0);
        Assert.Equal(1, f.Scalar("SELECT COUNT(*) FROM placed_cube_instances WHERE world_id = 1;"));

        TerrainResetResult result = f.Catalog.ResetTerrain(WorldId.Default);
        Assert.Equal(2, result.OverridesCleared);
        Assert.Equal(1, result.CellsKeptForPlacedCubes);
        Assert.True(result.RubbleRowsDeleted > 0);

        Assert.Equal(1, f.Scalar("SELECT COUNT(*) FROM terrain_cell_overrides WHERE world_id = 1;"));
        Assert.Equal(
            1,
            f.Scalar($"SELECT COUNT(*) FROM terrain_cell_overrides WHERE cell_x = {underCube.X} AND cell_y = {underCube.Y} AND cell_z = {underCube.Z};"));
        Assert.Equal(keptRubble, f.Scalar("SELECT COUNT(*) FROM cube_fracture_pieces WHERE world_id = 1;"));
        Assert.Equal(1, f.Scalar("SELECT COUNT(*) FROM placed_cube_instances WHERE world_id = 1;"));

        // The CLI prints the same numbers.
        Assert.Contains("kept=1", f.Cli("reset-terrain", "1", "--yes").Out);
    }

    [Fact]
    public void AfterARestart_NothingIsHealed_AndTheMapEqualsTheBase()
    {
        using TerrainCliFixture f = new();
        f.FractureTerrain(flat: 3, sculpture: 1);
        Assert.Equal(0, f.Cli("reset-terrain", "1", "--yes").Exit);

        WorldTerrainSource source = f.Catalog.ReadTerrainSource(WorldId.Default);
        Map baseMap = new(MapSources.ForWorld(source.Record.Descriptor, source.Seed), physics: null, headless: true);
        using TerrainRig rig = f.OpenRig(WorldId.Default);
        List<(string Category, string Message)> lines = rig.Restore();
        Assert.DoesNotContain(lines, line => line.Category == HealedCategory);
        Assert.Equal(baseMap.WorldChecksum, rig.Map.WorldChecksum);
        Assert.Empty(rig.Map.CaptureFracturePieces());
        Assert.Empty(rig.Map.FindInvariantViolations());
    }

    [Fact]
    public void Control_DeletingOnlyTheOverrideRows_IsHealedAtRestart_WhichIsWhyResetDropsTheRubble()
    {
        using TerrainCliFixture f = new();
        f.FractureTerrain(flat: 2);
        f.Exec("DELETE FROM terrain_cell_overrides WHERE world_id = 1;");

        using TerrainRig rig = f.OpenRig(WorldId.Default);
        List<(string Category, string Message)> lines = rig.Restore();
        Assert.Single(lines, line => line.Category == HealedCategory);
    }

    [Fact]
    public void AStoredWorld_ResetsToItsOwnChunks()
    {
        using TerrainCliFixture f = new();
        f.FractureTerrain(flat: 2);
        Assert.Equal(0, f.Cli("export", "1", "baked").Exit);
        WorldId stored = new(2);

        // Edits on the stored world (one more cell, with rubble).
        GeometryCellKey extra;
        using (TerrainRig rig = f.OpenRig(stored))
        {
            rig.Restore();
            extra = TerrainCliFixture.RaisedCells(rig.Map, sculpture: false).First();
            rig.FractureTerrain(extra);
        }

        Assert.Equal(1, f.Scalar("SELECT COUNT(*) FROM terrain_cell_overrides WHERE world_id = 2;"));
        long worldOneRubble = f.Scalar("SELECT COUNT(*) FROM cube_fracture_pieces WHERE world_id = 1;");
        long worldTwoRubble = f.Scalar("SELECT COUNT(*) FROM cube_fracture_pieces WHERE world_id = 2;");
        Assert.True(worldTwoRubble > worldOneRubble);

        (int exit, string line, _) = f.Cli("reset-terrain", "baked", "--yes");
        Assert.Equal(0, exit);
        Assert.Contains("cleared=1", line);

        // The baked-out cells of the source stay out (they are not in the chunks); the extra cell is back.
        using TerrainRig again = f.OpenRig(stored);
        List<(string Category, string Message)> lines = again.Restore();
        Assert.DoesNotContain(lines, l => l.Category == HealedCategory);
        Assert.True(again.Map.GeometryCells.ContainsKey(extra));
        Assert.True(again.Map.IsSourceCell(extra));
        IReadOnlyList<PersistedTerrainChunk> chunks = new SqliteTerrainChunkRepository(new SqliteWorldStore(f.Schema), stored).LoadTerrainChunks();
        Map fromChunks = new(new StoredMapSource(MapDefinition.ForWorld(f.Catalog.TryGet(stored)!.Descriptor), chunks), physics: null, headless: true);
        // The restored map (rubble included) stands on exactly the chunks' source cells.
        Assert.Equal(
            fromChunks.GeometryCells.Keys.Where(k => fromChunks.IsSourceCell(k)).OrderBy(k => (k.X, k.Z, k.Y)),
            again.Map.GeometryCells.Keys.Where(k => again.Map.IsSourceCell(k)).OrderBy(k => (k.X, k.Z, k.Y)));
        // The copied rubble names world 1's overrides, which the export does not copy: lost-override rubble
        // (fix loop 1, S4), so the reset drops it; world 1 is untouched.
        Assert.Equal(0, f.Scalar("SELECT COUNT(*) FROM cube_fracture_pieces WHERE world_id = 2;"));
        Assert.Equal(worldOneRubble, f.Scalar("SELECT COUNT(*) FROM cube_fracture_pieces WHERE world_id = 1;"));
    }

    // Recorded behaviour (not fixed in C): reset-terrain never touches players or enemies. A player or
    // enemy saved inside a cell whose terrain comes back is restored at the saved position, which is
    // now inside a standing terrain cube: restore does not relocate or log, and no code checks it. It
    // is left to the entity's own movement and collision resolution.
    [Fact]
    public void AnEnemyOrPlayerSavedInsideARestoredCell_IsNotMovedByResetOrRestore_Recorded()
    {
        using TerrainCliFixture f = new();
        GeometryCellKey cell = f.FractureTerrain(flat: 1)[0];
        float cx = cell.X + 0.5f;
        float cy = cell.Y + 0.5f;
        float cz = cell.Z + 0.5f;
        string center = string.Join(", ", cx.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture), cy.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture), cz.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture));
        f.Exec("INSERT OR IGNORE INTO player_profiles (player_name, display_name) VALUES ('alice', 'Alice');");
        f.Exec($"INSERT INTO player_world_state (world_id, player_name, position_x, position_y, position_z, facing_x, facing_y, facing_z) VALUES (1, 'alice', {center}, 0, 0, 1);");
        f.Exec($"INSERT INTO enemy_instances (world_id, object_id, enemy_kind, display_name, position_x, position_y, position_z, facing_x, facing_y, facing_z, current_hp, max_hp) VALUES (1, 800001, 'wolf', 'Wolf', {center}, 0, 0, 1, 10, 10);");

        Assert.Equal(0, f.Cli("reset-terrain", "1", "--yes").Exit);
        Assert.Equal(1, f.Scalar("SELECT COUNT(*) FROM player_world_state WHERE world_id = 1 AND player_name = 'alice';"));
        Assert.Equal(1, f.Scalar("SELECT COUNT(*) FROM enemy_instances WHERE world_id = 1 AND object_id = 800001;"));

        using TerrainRig rig = f.OpenRig(WorldId.Default);
        List<(string Category, string Message)> lines = rig.Restore();
        Assert.NotNull(rig.Map.Cubes[cell.X, cell.Y, cell.Z]);
        EnemyActorSnapshot enemy = Assert.Single(rig.Enemies.ActiveActors.Select(a => new EnemyActorSnapshot(a.Position.X, a.Position.Y, a.Position.Z)));
        Assert.Equal((cx, cy, cz), (enemy.X, enemy.Y, enemy.Z));
        Assert.DoesNotContain(lines, l => l.Category.Contains("enemy", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(rig.Map.FindInvariantViolations());
        output.WriteLine("recorded: a saved enemy/player inside a restored terrain cell keeps its saved position, inside the solid cube; no relocation, no log.");
    }

    private readonly record struct EnemyActorSnapshot(float X, float Y, float Z);

    [Fact]
    public void CliErrors_AndCatalogNotFound()
    {
        using TerrainCliFixture f = new();
        f.FractureTerrain(flat: 1);
        Assert.Equal(1, f.Cli("reset-terrain", "1").Exit);
        Assert.Equal(1, f.Cli("reset-terrain").Exit);
        Assert.Equal(1, f.Cli("reset-terrain", "1", "2", "--yes").Exit);
        Assert.Equal(2, f.Cli("reset-terrain", "77", "--yes").Exit);
        Assert.Equal(2, f.Cli("reset-terrain", "nope", "--yes").Exit);
        Assert.Throws<WorldNotFoundException>(() => f.Catalog.ResetTerrain(new WorldId(77)));
        Assert.Equal(1, f.Scalar("SELECT COUNT(*) FROM terrain_cell_overrides WHERE world_id = 1;"));
    }
}
