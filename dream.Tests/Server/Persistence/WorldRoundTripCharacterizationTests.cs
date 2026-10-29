using Dream.Simulation.World;
using System.Text.RegularExpressions;
using Dream.Server.Persistence;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.World.Geometry;
using Microsoft.Data.Sqlite;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Server.Persistence;

// Persistence wave A safety net (A5). Pins down what today's SQLite persistence writes and reads
// back, so the migration runner, the store and the per-aggregate repository split can be checked
// against it: every write path of the SqliteWorldPersistenceRepository facade, the profile
// repository and the world metadata goes into one file, the file is reopened through a fresh
// bootstrapper (EnsureSchema runs again), and every field is compared, including the text format
// of the stored updated_utc and created_utc values.
//
// Only behaviour that the wave keeps is pinned. A deleted combat_formulas seed row coming back on
// restart is deliberately not tested: with migrations the baseline runs once, so it no longer does.
public sealed class WorldRoundTripCharacterizationTests
{
    // SqliteSchemaBootstrapper.FormatUtc: round-trip ("O") format in UTC, always 28 characters.
    private static readonly Regex RoundTripUtc = new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z$");

    private static readonly DateTime CreatedUtc = new DateTime(2026, 3, 14, 15, 9, 26, DateTimeKind.Utc).AddTicks(5358979);

    [Fact]
    public void EveryWritePath_RoundTripsFieldByField_ThroughAFreshBootstrapper()
    {
        using CharacterizationDatabase database = new();
        WrittenWorld written = WriteEverything(database.CreateSchema());
        SqliteConnection.ClearAllPools();

        SqliteSchemaBootstrapper reopened = database.CreateSchema();
        AssertProfiles(reopened, written);
        AssertWorldState(reopened, written);
        AssertPlacedCubes(reopened, written);
        AssertFracturePieces(reopened, written);
        AssertEnemies(reopened, written);
        AssertItemsAndInventory(reopened, written);
        AssertMetadata(reopened);
        AssertCombatFormulas(reopened);
    }

    [Fact]
    public void StoredTimestamps_UseTheRoundTripFormat_AndReassignKeepsTheEnemyTimestamp()
    {
        using CharacterizationDatabase database = new();
        WriteEverything(database.CreateSchema());
        SqliteConnection.ClearAllPools();
        SqliteSchemaBootstrapper reopened = database.CreateSchema();

        string[] stamped =
        [
            "SELECT created_utc FROM player_profiles",
            "SELECT updated_utc FROM player_profiles",
            "SELECT updated_utc FROM player_world_state",
            "SELECT created_utc FROM placed_cube_instances",
            "SELECT updated_utc FROM placed_cube_instances",
            "SELECT created_utc FROM cube_fracture_pieces",
            "SELECT updated_utc FROM cube_fracture_pieces",
            "SELECT created_utc FROM enemy_instances",
            "SELECT updated_utc FROM enemy_instances",
            "SELECT created_utc FROM item_instances",
            "SELECT updated_utc FROM item_instances",
            "SELECT updated_utc FROM player_inventory_slots"
        ];
        foreach (string sql in stamped)
        {
            List<string> values = ReadStrings(reopened, sql);
            Assert.NotEmpty(values);
            Assert.All(values, value => Assert.Matches(RoundTripUtc, value));
        }

        // The given created time is stored as is, to the tick.
        Assert.All(
            ReadStrings(reopened, "SELECT created_utc FROM placed_cube_instances"),
            value => Assert.Equal(SqliteSchemaBootstrapper.FormatUtc(CreatedUtc), value));

        // The seed formulas keep the schema's strftime default (milliseconds), an upserted one the
        // round-trip format.
        Assert.Matches(
            new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$"),
            ReadStrings(reopened, "SELECT updated_utc FROM combat_formulas WHERE formula_key = 'player.max_health'").Single());
        Assert.Matches(
            RoundTripUtc,
            ReadStrings(reopened, "SELECT updated_utc FROM combat_formulas WHERE formula_key = 'player.min_damage'").Single());

        // ReassignEnemyId is a bare UPDATE of the key: the row keeps the time its save stamped.
        PersistedEnemyInstance moved = new SqliteWorldPersistenceRepository(reopened).LoadActiveEnemies().Single(enemy => enemy.ObjectId.AsInt() == 5);
        Assert.Equal(
            SqliteSchemaBootstrapper.FormatUtc(moved.UpdatedUtc),
            ReadStrings(reopened, "SELECT updated_utc FROM enemy_instances WHERE enemy_id = 5").Single());
    }

    [Fact]
    public void ASecondEnsureSchema_KeepsEveryRow()
    {
        using CharacterizationDatabase database = new();
        SqliteSchemaBootstrapper schema = database.CreateSchema();
        WriteEverything(schema);
        Dictionary<string, long> before = CountRows(schema);

        schema.EnsureSchema();
        SqliteConnection.ClearAllPools();
        Dictionary<string, long> after = CountRows(database.CreateSchema());

        Assert.Equal(before, after);
        Assert.Equal(2, after["player_profiles"]);
        Assert.Equal(1, after["player_world_state"]);
        Assert.Equal(2, after["placed_cube_instances"]);
        Assert.Equal(2, after["cube_fracture_pieces"]);
        Assert.Equal(2, after["enemy_instances"]);
        Assert.Equal(2, after["item_instances"]);
        Assert.Equal(2, after["player_inventory_slots"]);
        Assert.Equal(8, after["combat_formulas"]);
        // The seed and the clock live on the worlds row since migration 0003.
        Assert.Equal(0, after["world_metadata"]);
        Assert.Equal(1, after["worlds"]);
        Assert.Equal(0, after["player_profile_change_log"]);
    }

    private sealed record WrittenWorld(
        PersistedPlayerProfile Alpha,
        PersistedPlayerProfile Beta,
        PersistedPlayerWorldState AlphaState,
        PersistedPlacedCube CubeWithCenter,
        PersistedPlacedCube CubeWithoutCenter,
        PersistedCubeFracturePiece[] Pieces,
        PersistedEnemyInstance Kept,
        PersistedEnemyInstance Reassigned,
        PersistedItemInstance Held,
        PersistedItemInstance Dropped,
        DateTime StartedUtc,
        DateTime FinishedUtc);

    private static WrittenWorld WriteEverything(SqliteSchemaBootstrapper schema)
    {
        DateTime started = DateTime.UtcNow;
        SqlitePlayerProfileRepository profiles = new(schema);
        SqliteWorldPersistenceRepository world = new(schema);

        PersistedPlayerProfile alpha = new(
            "alpha",
            "Alpha The First",
            PlayerInteractionMode.Combat,
            new PersistedCreatureStats(87, 120, 19, 14, 16, 22, 7.5f, 3.25f, 5, 222),
            CreatedUtc,
            CreatedUtc);
        // Padded name and blank display name: the repository trims the key and falls back to it.
        PersistedPlayerProfile beta = new(
            "  beta  ",
            "   ",
            PlayerInteractionMode.Building,
            new PersistedCreatureStats(100, 100, 10, 10, 10, 10, 6f, 2f, 1, 0),
            default,
            default);
        profiles.Save(alpha);
        profiles.Save(beta);
        profiles.GetOrCreate("alpha");

        PersistedPlayerWorldState alphaState = new(
            " alpha ",
            new Vector3(12.5f, -3.25f, 1024.0625f),
            new Vector3(0f, 0.6f, -0.8f),
            77,
            9,
            new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        world.SavePlayerWorldState(alphaState with { CurrentHealth = 1 });
        world.SavePlayerWorldState(alphaState);

        PersistedPlacedCube withCenter = new(
            new ObjectId(10),
            "alpha",
            new Vector3(4f, 5f, 6f),
            GeometryShapeKind.SolidCube,
            GeometryMaterialId.Stone,
            40,
            50,
            CreatedUtc,
            default,
            new Vector3(4.5f, 5.5f, 6.5f));
        PersistedPlacedCube withoutCenter = withCenter with
        {
            ObjectId = new ObjectId(11),
            CellPosition = new Vector3(-7f, 0f, 3f),
            MaterialId = GeometryMaterialId.Dirt,
            CurrentHealth = 1,
            MaxHealth = 1,
            CenterPosition = null
        };
        world.SavePlacedCube(withCenter with { CurrentHealth = 50 });
        world.SavePlacedCube(withCenter);
        world.SavePlacedCube(withoutCenter);
        world.SavePlacedCube(withCenter with { ObjectId = new ObjectId(12) });
        world.DeletePlacedCube(new ObjectId(12));

        PersistedCubeFracturePiece[] pieces =
        [
            new(ObjectId.None, new ObjectId(10), 0, "alpha", new Vector3(4f, 5f, 6f), new Vector3(4.25f, 5.25f, 6.25f), 0.5f, GeometryMaterialId.Stone, CreatedUtc, default),
            new(ObjectId.None, new ObjectId(10), 1, " alpha ", new Vector3(4f, 5f, 6f), new Vector3(4.75f, 5.25f, 6.25f), 0.25f, GeometryMaterialId.Stone, CreatedUtc, default)
        ];
        world.SaveCubeFracturePiece(pieces[0] with { SideLength = 9f });
        foreach (PersistedCubeFracturePiece piece in pieces)
        {
            world.SaveCubeFracturePiece(piece);
        }

        world.SaveCubeFracturePiece(pieces[0] with { OriginObjectId = new ObjectId(11) });
        world.DeleteCubeFracturePieces(new ObjectId(11));

        PersistedEnemyInstance kept = new(
            new ObjectId(1),
            "slime",
            "Green Slime",
            new Vector3(30f, 1f, -30f),
            new Vector3(1f, 0f, 0f),
            new PersistedCreatureStats(33, 40, 8, 6, 9, 0, 2.8f, 0.95f, 3),
            CreatedUtc,
            default);
        PersistedEnemyInstance reassigned = kept with
        {
            ObjectId = new ObjectId(2),
            EnemyKind = "wolf",
            DisplayName = "Grey Wolf",
            Position = new Vector3(-1.5f, 2f, 8f),
            Stats = new PersistedCreatureStats(60, 60, 14, 12, 11, 4, 4.5f, 1.25f, 7)
        };
        world.SaveEnemyInstance(kept);
        world.SaveEnemyInstance(reassigned);
        world.SaveEnemyInstance(kept with { ObjectId = new ObjectId(3) });
        world.DeleteEnemyInstance(new ObjectId(3));
        world.ReassignEnemyId(2, 5);

        PersistedItemInstance held = new(100, "sword", "Iron Sword", "sword_iron", "alpha", null, "Inventory", 1, CreatedUtc, default);
        PersistedItemInstance dropped = new(101, "stone", "Stone", "stone_small", null, new Vector3(1.5f, 0.25f, -2f), "World", 12, CreatedUtc, default);
        world.SaveItemInstance(held);
        world.SaveItemInstance(dropped);
        world.SaveItemInstance(held with { ItemInstanceId = 102 });
        world.DeleteItemInstance(102);

        world.SaveInventorySlot(new PersistedInventorySlot(" alpha ", 0, 101, default));
        world.SaveInventorySlot(new PersistedInventorySlot("alpha", 0, 100, default));
        world.SaveInventorySlot(new PersistedInventorySlot("alpha", 3, 100, default));
        world.SaveInventorySlot(new PersistedInventorySlot("beta", 0, 101, default));
        world.DeleteInventorySlots(" beta ");

        SqliteWorldMetadataRepository metadata = new(schema);
        Assert.Equal(new WorldSimulationSeed(0xFEDC_BA98_7654_3210UL, Created: true), metadata.GetOrCreateSimulationSeed(() => 0xFEDC_BA98_7654_3210UL));
        new SqlitePersistenceCheckpointWriter(schema).Write(PersistenceCheckpoint.Empty with { WorldTimeTicks = 1_234_567_890_123L });

        SqliteCombatFormulaRepository formulas = new(schema);
        formulas.Upsert(new CombatFormulaDefinition("player.min_damage", "7 + strength * 3", "Edited minimum damage.", true, default));
        formulas.Upsert(new CombatFormulaDefinition("player.critical_chance", "0.15", "Critical hit chance.", false, default));

        return new WrittenWorld(
            alpha,
            beta,
            alphaState,
            withCenter,
            withoutCenter,
            pieces,
            kept,
            reassigned with { ObjectId = new ObjectId(5) },
            held,
            dropped,
            started,
            DateTime.UtcNow);
    }

    private static void AssertProfiles(SqliteSchemaBootstrapper schema, WrittenWorld written)
    {
        SqlitePlayerProfileRepository profiles = new(schema);

        PersistedPlayerProfile alpha = Assert.NotNull(profiles.TryGet("  alpha "));
        Assert.Equal("alpha", alpha.PlayerName);
        Assert.Equal(written.Alpha.DisplayName, alpha.DisplayName);
        Assert.Equal(written.Alpha.InteractionMode, alpha.InteractionMode);
        Assert.Equal(written.Alpha.Stats, alpha.Stats);
        Assert.Equal(CreatedUtc, alpha.CreatedUtc);
        AssertStampedDuringWrite(alpha.UpdatedUtc, written);

        PersistedPlayerProfile beta = Assert.NotNull(profiles.TryGet("beta"));
        Assert.Equal("beta", beta.PlayerName);
        Assert.Equal("beta", beta.DisplayName);
        Assert.Equal(PlayerInteractionMode.Building, beta.InteractionMode);
        Assert.Equal(written.Beta.Stats, beta.Stats);
        // A default created time is replaced by the save time.
        AssertStampedDuringWrite(beta.CreatedUtc, written);
        Assert.Equal(beta.CreatedUtc, beta.UpdatedUtc);

        Assert.Null(profiles.TryGet("gamma"));
    }

    private static void AssertWorldState(SqliteSchemaBootstrapper schema, WrittenWorld written)
    {
        SqliteWorldPersistenceRepository world = new(schema);
        PersistedPlayerWorldState state = Assert.NotNull(world.TryGetPlayerWorldState("alpha "));
        Assert.Equal("alpha", state.PlayerName);
        Assert.Equal(written.AlphaState.Position, state.Position);
        Assert.Equal(written.AlphaState.Facing, state.Facing);
        Assert.Equal(written.AlphaState.CurrentHealth, state.CurrentHealth);
        Assert.Equal(written.AlphaState.CurrentEnergy, state.CurrentEnergy);
        // The row carries the state's UpdatedUtc (its capture time), not the write time, so the
        // checkpoint guards compare capture times.
        Assert.Equal(written.AlphaState.UpdatedUtc, state.UpdatedUtc);
        Assert.Equal(DateTimeKind.Utc, state.UpdatedUtc.Kind);

        Assert.Null(world.TryGetPlayerWorldState("beta"));
    }

    private static void AssertPlacedCubes(SqliteSchemaBootstrapper schema, WrittenWorld written)
    {
        IReadOnlyList<PersistedPlacedCube> cubes = new SqliteWorldPersistenceRepository(schema).LoadPlacedCubes();
        Assert.Equal(new[] { 10, 11 }, cubes.Select(cube => cube.ObjectId.AsInt()));
        AssertCube(written.CubeWithCenter, cubes[0], written);
        AssertCube(written.CubeWithoutCenter, cubes[1], written);
        Assert.Null(cubes[1].CenterPosition);
    }

    private static void AssertCube(PersistedPlacedCube expected, PersistedPlacedCube actual, WrittenWorld written)
    {
        Assert.Equal(expected.ObjectId.AsInt(), actual.ObjectId.AsInt());
        Assert.Equal(expected.OwnerPlayerName, actual.OwnerPlayerName);
        Assert.Equal(expected.CellPosition, actual.CellPosition);
        Assert.Equal(expected.CenterPosition, actual.CenterPosition);
        Assert.Equal(expected.ShapeKind, actual.ShapeKind);
        Assert.Equal(expected.MaterialId, actual.MaterialId);
        Assert.Equal(expected.CurrentHealth, actual.CurrentHealth);
        Assert.Equal(expected.MaxHealth, actual.MaxHealth);
        Assert.Equal(CreatedUtc, actual.CreatedUtc);
        AssertStampedDuringWrite(actual.UpdatedUtc, written);
    }

    private static void AssertFracturePieces(SqliteSchemaBootstrapper schema, WrittenWorld written)
    {
        IReadOnlyList<PersistedCubeFracturePiece> pieces = new SqliteWorldPersistenceRepository(schema).LoadCubeFracturePieces();
        Assert.Equal(2, pieces.Count);
        for (int i = 0; i < pieces.Count; i++)
        {
            PersistedCubeFracturePiece expected = written.Pieces[i];
            PersistedCubeFracturePiece actual = pieces[i];
            Assert.Equal(expected.OriginObjectId.AsInt(), actual.OriginObjectId.AsInt());
            Assert.Equal(expected.PieceIndex, actual.PieceIndex);
            Assert.Equal("alpha", actual.OwnerPlayerName);
            Assert.Equal(expected.ParentCell, actual.ParentCell);
            Assert.Equal(expected.Center, actual.Center);
            Assert.Equal(expected.SideLength, actual.SideLength);
            Assert.Equal(expected.MaterialId, actual.MaterialId);
            // An upsert keeps the first insert's created time.
            Assert.Equal(CreatedUtc, actual.CreatedUtc);
            AssertStampedDuringWrite(actual.UpdatedUtc, written);
        }
    }

    private static void AssertEnemies(SqliteSchemaBootstrapper schema, WrittenWorld written)
    {
        IReadOnlyList<PersistedEnemyInstance> enemies = new SqliteWorldPersistenceRepository(schema).LoadActiveEnemies();
        Assert.Equal(new[] { 1, 5 }, enemies.Select(enemy => enemy.ObjectId.AsInt()));
        AssertEnemy(written.Kept, enemies[0], written);
        AssertEnemy(written.Reassigned, enemies[1], written);
    }

    private static void AssertEnemy(PersistedEnemyInstance expected, PersistedEnemyInstance actual, WrittenWorld written)
    {
        Assert.Equal(expected.ObjectId.AsInt(), actual.ObjectId.AsInt());
        Assert.Equal(expected.EnemyKind, actual.EnemyKind);
        Assert.Equal(expected.DisplayName, actual.DisplayName);
        Assert.Equal(expected.Position, actual.Position);
        Assert.Equal(expected.Facing, actual.Facing);
        // Enemies have no experience column: it reads back as the default 0.
        Assert.Equal(expected.Stats with { Experience = 0 }, actual.Stats);
        Assert.Equal(CreatedUtc, actual.CreatedUtc);
        AssertStampedDuringWrite(actual.UpdatedUtc, written);
    }

    private static void AssertItemsAndInventory(SqliteSchemaBootstrapper schema, WrittenWorld written)
    {
        SqliteWorldPersistenceRepository world = new(schema);
        IReadOnlyList<PersistedItemInstance> items = world.LoadItemInstances();
        Assert.Equal(new[] { 100L, 101L }, items.Select(item => item.ItemInstanceId));
        AssertItem(written.Held, items[0], written);
        AssertItem(written.Dropped, items[1], written);
        Assert.Equal("alpha", items[0].OwnerPlayerName);
        Assert.Null(items[0].WorldPosition);
        Assert.Null(items[1].OwnerPlayerName);
        Assert.Equal(101L, world.GetMaxItemInstanceId());

        IReadOnlyList<PersistedInventorySlot> slots = world.LoadInventorySlots();
        Assert.Equal(new[] { ("alpha", 0, 100L), ("alpha", 3, 100L) }, slots.Select(slot => (slot.PlayerName, slot.SlotIndex, slot.ItemInstanceId)));
        Assert.All(slots, slot => AssertStampedDuringWrite(slot.UpdatedUtc, written));

        // One item in two slots is listed once; a player with no slots loads empty.
        PersistedPlayerInventory inventory = world.LoadPlayerInventory(" alpha ");
        Assert.Equal(slots, inventory.Slots);
        PersistedItemInstance only = Assert.Single(inventory.Items);
        Assert.Equal(items[0], only);
        PersistedPlayerInventory empty = world.LoadPlayerInventory("beta");
        Assert.Empty(empty.Slots);
        Assert.Empty(empty.Items);
    }

    private static void AssertItem(PersistedItemInstance expected, PersistedItemInstance actual, WrittenWorld written)
    {
        Assert.Equal(expected.ItemInstanceId, actual.ItemInstanceId);
        Assert.Equal(expected.ItemKind, actual.ItemKind);
        Assert.Equal(expected.DisplayName, actual.DisplayName);
        Assert.Equal(expected.VisualId, actual.VisualId);
        Assert.Equal(expected.WorldPosition, actual.WorldPosition);
        Assert.Equal(expected.State, actual.State);
        Assert.Equal(expected.StackCount, actual.StackCount);
        Assert.Equal(CreatedUtc, actual.CreatedUtc);
        Assert.Equal(expected.OwnerPlayerName, actual.OwnerPlayerName);
        AssertStampedDuringWrite(actual.UpdatedUtc, written);
    }

    private static void AssertMetadata(SqliteSchemaBootstrapper schema)
    {
        SqliteWorldMetadataRepository metadata = new(schema);
        Assert.True(metadata.TryGetSimulationSeed(out ulong seed));
        Assert.Equal(0xFEDC_BA98_7654_3210UL, seed);
        Assert.Equal(new WorldSimulationSeed(seed, Created: false), metadata.GetOrCreateSimulationSeed(() => 1UL));
        Assert.True(metadata.TryGetWorldTimeTicks(out long ticks));
        Assert.Equal(1_234_567_890_123L, ticks);
        Assert.Equal(
            new[] { "18364758544493064720", "1234567890123" },
            ReadStrings(schema, "SELECT seed FROM worlds WHERE world_id = 1 UNION ALL SELECT CAST(world_time_ticks AS TEXT) FROM worlds WHERE world_id = 1"));
    }

    private static void AssertCombatFormulas(SqliteSchemaBootstrapper schema)
    {
        SqliteCombatFormulaRepository formulas = new(schema);
        CombatFormulaDefinition edited = Assert.NotNull(formulas.TryGet("player.min_damage"));
        Assert.Equal("7 + strength * 3", edited.ExpressionText);
        Assert.Equal("Edited minimum damage.", edited.Description);
        Assert.True(edited.Enabled);

        CombatFormulaDefinition disabled = Assert.NotNull(formulas.TryGet("player.critical_chance"));
        Assert.False(disabled.Enabled);
        Assert.DoesNotContain(formulas.LoadAllEnabled(), formula => formula.FormulaKey == "player.critical_chance");
        Assert.Equal(7, formulas.LoadAllEnabled().Count);
    }

    // Repositories stamp updated_utc (and a default created_utc) with DateTime.UtcNow at write time.
    private static void AssertStampedDuringWrite(DateTime value, WrittenWorld written)
    {
        Assert.Equal(DateTimeKind.Utc, value.Kind);
        Assert.InRange(value, written.StartedUtc, written.FinishedUtc);
    }

    private static List<string> ReadStrings(SqliteSchemaBootstrapper schema, string sql)
    {
        using SqliteConnection connection = schema.CreateOpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> values = [];
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static Dictionary<string, long> CountRows(SqliteSchemaBootstrapper schema)
    {
        string[] tables =
        [
            "player_profiles",
            "player_world_state",
            "placed_cube_instances",
            "cube_fracture_pieces",
            "enemy_instances",
            "item_instances",
            "player_inventory_slots",
            "combat_formulas",
            "world_metadata",
            "worlds",
            "player_profile_change_log"
        ];
        using SqliteConnection connection = schema.CreateOpenConnection();
        Dictionary<string, long> counts = [];
        foreach (string table in tables)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table};";
            counts[table] = (long)command.ExecuteScalar()!;
        }

        return counts;
    }

    private sealed class CharacterizationDatabase : IDisposable
    {
        private readonly string _rootDirectory;
        private readonly string _databasePath;
        private readonly string _schemaPath;

        public CharacterizationDatabase()
        {
            _rootDirectory = Path.Combine(Path.GetTempPath(), $"dream-roundtrip-characterization-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_rootDirectory);
            _databasePath = Path.Combine(_rootDirectory, "persistence.sqlite");
            _schemaPath = Path.Combine(Dream.Tests.Load.LoadPaths.FindRepoRoot(), "dream", "config", "player-profiles.init.sql");
        }

        // A new bootstrapper on the same file, as a server restart builds one.
        public SqliteSchemaBootstrapper CreateSchema()
        {
            SqliteSchemaBootstrapper schema = new(_databasePath, _schemaPath);
            schema.EnsureSchema();
            return schema;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_rootDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
