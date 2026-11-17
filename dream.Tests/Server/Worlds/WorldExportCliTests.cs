using System.Globalization;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;
using Dream.Server.Persistence.Repositories;
using Dream.Server.Persistence.Store;
using Dream.Server.Worlds;
using Dream.Simulation.Persistence;
using Dream.Simulation.Physics;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Simulation.World.MapSources;
using Dream.Simulation.World.Terrain;

namespace Dream.Tests.Server.Worlds;

// Persistence wave E (C): `--worlds export <src> <new> [--terrain-only]` bakes a world's current base
// terrain (source minus Removed overrides) into a new stored world.
public sealed class WorldExportCliTests(ITestOutputHelper output)
{
    private static IReadOnlyList<PersistedTerrainChunk> LoadChunks(TerrainCliFixture f, WorldId id) =>
        new SqliteTerrainChunkRepository(new SqliteWorldStore(f.Schema), id).LoadTerrainChunks();

    [Fact]
    public void ExportOfAGeneratedWorldWithRemovals_BakesThemOut_KeepsRubble_AndBuildsTheSameRuntime()
    {
        using TerrainCliFixture f = new();
        List<GeometryCellKey> removed = f.FractureTerrain(flat: 3, sculpture: 1);
        long sourceRubble = f.Scalar("SELECT COUNT(*) FROM cube_fracture_pieces WHERE world_id = 1;");
        Assert.True(sourceRubble > 0);

        (int exit, string created, string error) = f.Cli("export", "1", "baked");
        Assert.Equal(0, exit);
        Assert.Contains("stop the server hosting this world first", error);
        string[] fields = created.TrimEnd().Split('\t');
        Assert.Equal(["2", "baked", "56x8x56", "stored:natural:1"], fields[..4]);

        // The new world: no overrides, the source's rubble, a stored map source, the same seed.
        Assert.Equal(0, f.Scalar("SELECT COUNT(*) FROM terrain_cell_overrides WHERE world_id = 2;"));
        Assert.Equal(sourceRubble, f.Scalar("SELECT COUNT(*) FROM cube_fracture_pieces WHERE world_id = 2;"));
        Assert.Equal(1, f.Scalar("SELECT COUNT(*) FROM worlds WHERE world_id = 2 AND map_source = 'stored';"));
        Assert.Equal(f.Catalog.TryGet(WorldId.Default)!.Seed, f.Catalog.TryGet(new WorldId(2))!.Seed);
        Assert.Equal(
            f.Scalar("SELECT next_object_id FROM worlds WHERE world_id = 1;"),
            f.Scalar("SELECT next_object_id FROM worlds WHERE world_id = 2;"));
        Assert.Equal(
            f.Scalar("SELECT COUNT(*) FROM terrain_cell_overrides WHERE world_id = 1;"),
            removed.Count);

        // The chunks omit the removed cells.
        IReadOnlyList<PersistedTerrainChunk> chunks = LoadChunks(f, new WorldId(2));
        WorldRecord stored = f.Catalog.TryGet(new WorldId(2))!;
        MapDefinition definition = MapDefinition.ForWorld(stored.Descriptor);
        Map fromChunks = new(new StoredMapSource(definition, chunks), physics: null, headless: true);
        foreach (GeometryCellKey cell in removed)
        {
            Assert.False(fromChunks.GeometryCells.ContainsKey(cell), $"{cell} should be baked out");
        }

        // A runtime built from the export (C0 plumbing) equals the restored source runtime.
        using TerrainRig source = f.OpenRig(WorldId.Default);
        source.Restore();
        using WorldSessionRuntime runtime = new(
            stored.Descriptor,
            new OpenTK.Mathematics.Vector3(8, 2, 8),
            new SceneEventHub(),
            simulationSeed: stored.Seed!.Value,
            storedTerrain: chunks);
        // (The checksum covers restored rubble, which a bare runtime has not restored yet: compare it
        // with the generated map minus the removals, then the restored worlds with each other.)
        WorldTerrainSource generated = f.Catalog.ReadTerrainSource(WorldId.Default);
        Map expected = new(MapSources.ForWorld(generated.Record.Descriptor, generated.Seed), physics: null, headless: true);
        foreach (PersistedTerrainCellOverride o in generated.Overrides)
        {
            Assert.True(expected.RemoveSourceCell(o.Cell));
        }

        Assert.Equal(expected.WorldChecksum, runtime.Map.WorldChecksum);
        Assert.Equal(expected.GeometryCells.Count, runtime.Map.GeometryCells.Count);

        // And the stored world restores its rubble over its own chunks.
        using TerrainRig restored = f.OpenRig(new WorldId(2));
        restored.Restore();
        Assert.Equal(source.Map.WorldChecksum, restored.Map.WorldChecksum);
        Assert.Equal(source.Map.CaptureFracturePieces().Count, restored.Map.CaptureFracturePieces().Count);
        Assert.Empty(restored.Map.FindInvariantViolations());
    }

    [Fact]
    public void Export_IsDeterministic_TwoExportsGiveIdenticalBlobs()
    {
        using TerrainCliFixture f = new();
        f.FractureTerrain(flat: 2, sculpture: 1);

        Assert.Equal(0, f.Cli("export", "1", "one").Exit);
        Assert.Equal(0, f.Cli("export", "default", "two").Exit);
        IReadOnlyList<PersistedTerrainChunk> a = LoadChunks(f, new WorldId(2));
        IReadOnlyList<PersistedTerrainChunk> b = LoadChunks(f, new WorldId(3));
        Assert.NotEmpty(a);
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.Equal((a[i].ChunkX, a[i].ChunkZ, a[i].FormatVersion, a[i].CellCount), (b[i].ChunkX, b[i].ChunkZ, b[i].FormatVersion, b[i].CellCount));
            Assert.Equal(a[i].Data, b[i].Data);
        }
    }

    [Fact]
    public void ExportingAStoredWorld_Works_AndListShowsTheStoredColumn()
    {
        using TerrainCliFixture f = new();
        f.FractureTerrain(flat: 2);
        Assert.Equal(0, f.Cli("export", "1", "baked").Exit);

        // Remove one more cell of the stored world through an override, then export that.
        WorldTerrainSource bakedSource = f.Catalog.ReadTerrainSource(new WorldId(2));
        Assert.NotNull(bakedSource.StoredChunks);
        using (TerrainRig rig = f.OpenRig(new WorldId(2)))
        {
            rig.Restore();
            GeometryCellKey extra = TerrainCliFixture.RaisedCells(rig.Map, sculpture: false).First();
            rig.FractureTerrain(extra);
            Assert.Equal(0, f.Cli("export", "baked", "baked2").Exit);
            IReadOnlyList<PersistedTerrainChunk> chunks = LoadChunks(f, new WorldId(3));
            Map map = new(
                new StoredMapSource(MapDefinition.ForWorld(f.Catalog.TryGet(new WorldId(3))!.Descriptor), chunks),
                physics: null,
                headless: true);
            Assert.False(map.GeometryCells.ContainsKey(extra));
        }

        (int exit, string listed, _) = f.Cli("list");
        Assert.Equal(0, exit);
        string[] lines = listed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(3, lines.Length);
        Assert.Equal("natural:1", lines[0].Split('\t')[3]);
        Assert.Equal("stored:natural:1", lines[1].Split('\t')[3]);
        Assert.Equal("stored:natural:1", lines[2].Split('\t')[3]);
        Assert.Equal(5, lines[2].Split('\t').Length);
    }

    [Fact]
    public void TerrainOnlyExport_HasOnlyChunks_ANewSeed_AndWorldTimeZero()
    {
        using TerrainCliFixture f = new();
        f.FractureTerrain(flat: 2);
        f.Exec("UPDATE worlds SET world_time_ticks = 4242 WHERE world_id = 1;");
        f.Exec("INSERT INTO enemy_instances (world_id, object_id, enemy_kind, display_name, position_x, position_y, position_z, facing_x, facing_y, facing_z, current_hp, max_hp) VALUES (1, 700000, 'slime', 'Slime', 1, 1, 1, 0, 0, 1, 10, 10);");

        (int exit, string created, _) = f.Cli("export", "1", "template", "--terrain-only");
        Assert.Equal(0, exit);
        Assert.Equal(["2", "template", "56x8x56", "stored:natural:1", "0"], created.TrimEnd().Split('\t'));

        foreach (string table in new[]
        {
            "player_world_state", "placed_cube_instances", "cube_fracture_pieces", "enemy_instances",
            "item_instances", "player_inventory_slots", "player_starter_grants", "terrain_cell_overrides"
        })
        {
            Assert.Equal(0, f.Scalar($"SELECT COUNT(*) FROM {table} WHERE world_id = 2;"));
        }

        Assert.True(f.Scalar("SELECT COUNT(*) FROM terrain_chunks WHERE world_id = 2;") > 0);
        Assert.Equal(1, f.Scalar("SELECT next_object_id FROM worlds WHERE world_id = 2;"));
        Assert.NotEqual(f.Catalog.TryGet(WorldId.Default)!.Seed, f.Catalog.TryGet(new WorldId(2))!.Seed);
        Assert.NotNull(f.Catalog.TryGet(new WorldId(2))!.Seed);
        Assert.Equal(0, f.Catalog.TryGet(new WorldId(2))!.WorldTimeTicks);
        Assert.Equal(4242, f.Catalog.TryGet(WorldId.Default)!.WorldTimeTicks);
    }

    [Fact]
    public void CorruptStoredTerrain_ExitsFour_AndCreatesNothing()
    {
        using TerrainCliFixture f = new();
        Assert.Equal(0, f.Cli("export", "1", "baked").Exit);

        // A stored world with a chunk missing cannot be baked (StoredMapSource coverage rule).
        f.Exec("DELETE FROM terrain_chunks WHERE world_id = 2 AND chunk_x = 1 AND chunk_z = 1;");
        (int exit, _, string error) = f.Cli("export", "baked", "again");
        Assert.Equal(WorldCli.ExitTerrainInvalid, exit);
        Assert.Equal(4, WorldCli.ExitTerrainInvalid);
        Assert.Contains("cannot be exported", error);
        Assert.Null(f.Catalog.TryGetByName("again"));

        // So does a garbage blob.
        f.Exec("UPDATE terrain_chunks SET data = zeroblob(40) WHERE world_id = 2 AND chunk_x = 0 AND chunk_z = 0;");
        Assert.Equal(WorldCli.ExitTerrainInvalid, f.Cli("export", "baked", "again").Exit);
        Assert.Null(f.Catalog.TryGetByName("again"));
    }

    [Fact]
    public void BakeOfOversizedChunks_ThrowsInvalidData()
    {
        // The hosting limits: one blob over MaxTerrainBlobBytes is rejected before anything is written.
        using TerrainCliFixture f = new();
        WorldTerrainSource source = f.Catalog.ReadTerrainSource(WorldId.Default);
        BakedTerrain baked = WorldTerrainBaker.Bake(source.Record, source.Seed, null, []);
        Assert.Equal(0, baked.RemovedCells);
        Assert.Equal(0, baked.SkippedOverrides);
        Assert.Equal(baked.Chunks.Sum(c => (long)c.Data.Length), baked.Bytes);

        Dream.Multiplayer.Protocol.WorldTransferLimits limits = Dream.Multiplayer.Protocol.WorldTransferLimits.Default;
        PersistedTerrainChunk huge = baked.Chunks[0] with { Data = new byte[limits.MaxTerrainBlobBytes + 1] };
        WorldDescriptor stored = source.Record.Descriptor with { MapSource = WorldMapSource.Stored };
        Assert.Throws<InvalidDataException>(() =>
            Dream.Multiplayer.Protocol.TerrainTransferPayload.Create(stored, [huge, .. baked.Chunks.Skip(1)]));
    }

    [Fact]
    public void ReplacedOverrides_AreCountedAndSkipped()
    {
        using TerrainCliFixture f = new();
        WorldTerrainSource source = f.Catalog.ReadTerrainSource(WorldId.Default);
        DateTime now = DateTime.UtcNow;
        PersistedTerrainCellOverride replaced = new(
            new ObjectId(5), new GeometryCellKey(2, 3, 2), TerrainCellOverrideKind.Replaced, [1], now, now);
        PersistedTerrainCellOverride outside = new(
            new ObjectId(6), new GeometryCellKey(5000, 3, 5000), TerrainCellOverrideKind.Removed, null, now, now);
        BakedTerrain baked = WorldTerrainBaker.Bake(source.Record, source.Seed, null, [replaced, outside]);
        Assert.Equal(0, baked.RemovedCells);
        Assert.Equal(2, baked.SkippedOverrides);
    }

    [Fact]
    public void NameConflictAndUsageErrors()
    {
        using TerrainCliFixture f = new();
        Assert.Equal(0, f.Cli("export", "1", "baked").Exit);
        long worlds = f.Catalog.List().Count;

        Assert.Equal(2, f.Cli("export", "1", "BAKED").Exit);
        Assert.Equal(2, f.Cli("export", "77", "x").Exit);
        Assert.Equal(2, f.Cli("export", "nosuchworld", "x").Exit);
        Assert.Equal(1, f.Cli("export").Exit);
        Assert.Equal(1, f.Cli("export", "1").Exit);
        Assert.Equal(1, f.Cli("export", "1", "a", "b").Exit);
        Assert.Equal(1, f.Cli("export", "1", "x", "--bogus").Exit);
        Assert.Equal(1, f.Cli("export", "0", "x").Exit);
        Assert.Equal(1, f.Cli("export", "1", "123").Exit);
        Assert.Equal(1, f.Cli("export", "1", new string('n', 65)).Exit);
        Assert.Equal(worlds, f.Catalog.List().Count);
    }

    [Fact]
    public void Timings_AtWorld10k_AreReported()
    {
        using TerrainCliFixture f = new();
        WorldRecord big = f.Catalog.Create(new WorldCreateOptions("big", 100));
        WorldId id = big.Descriptor.Id;
        WorldTerrainSource source = f.Catalog.ReadTerrainSource(id);
        Map map = new(MapSources.ForWorld(big.Descriptor, source.Seed), physics: null, headless: true);
        List<GeometryCellKey> cells = TerrainCliFixture.RaisedCells(map, sculpture: false)
            .Concat(TerrainCliFixture.RaisedCells(map, sculpture: true)).Take(628).ToList();
        Assert.Equal(628, cells.Count);
        System.Text.StringBuilder sql = new();
        long objectId = 1000;
        foreach (GeometryCellKey cell in cells)
        {
            sql.Append(CultureInfo.InvariantCulture,
                $"INSERT INTO terrain_cell_overrides (world_id, object_id, cell_x, cell_y, cell_z, kind) VALUES ({id.Value}, {objectId++}, {cell.X}, {cell.Y}, {cell.Z}, 0);");
        }

        f.Exec(sql.ToString());

        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        (int exit, _, _) = f.Cli("export", "big", "bigstored");
        long exportMs = watch.ElapsedMilliseconds;
        Assert.Equal(0, exit);

        watch.Restart();
        (exit, string reset, _) = f.Cli("reset-terrain", "big", "--yes");
        long resetMs = watch.ElapsedMilliseconds;
        Assert.Equal(0, exit);
        Assert.Contains("cleared=628", reset);
        output.WriteLine($"World10k export (read, bake, write) {exportMs} ms; reset-terrain with 628 overrides {resetMs} ms; chunks {LoadChunks(f, new WorldId(id.Value + 1)).Count}");
    }
}
