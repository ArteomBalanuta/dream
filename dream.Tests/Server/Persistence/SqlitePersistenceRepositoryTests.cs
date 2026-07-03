using Dream.Server.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Server.Persistence;

public sealed class SqlitePersistenceRepositoryTests
{
    [Fact]
    public void SqlitePlayerProfileRepository_Save_RoundTripsPersistedProfile()
    {
        using TempPersistenceDatabase database = new();
        using SqlitePlayerProfileRepository repository = new(database.Schema);
        DateTime createdUtc = new(2026, 7, 4, 10, 30, 0, DateTimeKind.Utc);
        DateTime saveStartedUtc = DateTime.UtcNow;
        PersistedPlayerProfile profile = new(
            "player-one",
            "Player One",
            PlayerInteractionMode.Combat,
            new PersistedCreatureStats(
                CurrentHealth: 87,
                MaxHealth: 120,
                Strength: 19,
                Agility: 14,
                Vitality: 16,
                Energy: 22,
                MovementSpeed: 7.5f,
                AttackSpeed: 3.25f,
                Level: 5),
            createdUtc,
            createdUtc);

        repository.Save(profile);
        PersistedPlayerProfile? loaded = repository.TryGet(" player-one ");
        DateTime saveFinishedUtc = DateTime.UtcNow;

        Assert.True(loaded.HasValue);
        Assert.Equal("player-one", loaded.Value.PlayerName);
        Assert.Equal("Player One", loaded.Value.DisplayName);
        Assert.Equal(PlayerInteractionMode.Combat, loaded.Value.InteractionMode);
        Assert.Equal(87, loaded.Value.Stats.CurrentHealth);
        Assert.Equal(120, loaded.Value.Stats.MaxHealth);
        Assert.Equal(19, loaded.Value.Stats.Strength);
        Assert.Equal(14, loaded.Value.Stats.Agility);
        Assert.Equal(16, loaded.Value.Stats.Vitality);
        Assert.Equal(22, loaded.Value.Stats.Energy);
        Assert.Equal(7.5f, loaded.Value.Stats.MovementSpeed);
        Assert.Equal(3.25f, loaded.Value.Stats.AttackSpeed);
        Assert.Equal(5, loaded.Value.Stats.Level);
        Assert.Equal(createdUtc, loaded.Value.CreatedUtc);
        Assert.InRange(loaded.Value.UpdatedUtc, saveStartedUtc, saveFinishedUtc);
    }

    [Fact]
    public void SqlitePlayerProfileRepository_GetOrCreate_CreatesDefaultProfileOnce()
    {
        using TempPersistenceDatabase database = new();
        using SqlitePlayerProfileRepository repository = new(database.Schema);

        PersistedPlayerProfile created = repository.GetOrCreate(" player-two ");
        PersistedPlayerProfile loaded = repository.GetOrCreate("player-two");

        Assert.Equal("player-two", created.PlayerName);
        Assert.Equal("player-two", created.DisplayName);
        Assert.Equal(PlayerInteractionMode.Building, created.InteractionMode);
        Assert.Equal(100, created.Stats.CurrentHealth);
        Assert.Equal(100, created.Stats.MaxHealth);
        Assert.Equal(10, created.Stats.Strength);
        Assert.Equal(10, created.Stats.Agility);
        Assert.Equal(10, created.Stats.Vitality);
        Assert.Equal(10, created.Stats.Energy);
        Assert.Equal(6f, created.Stats.MovementSpeed);
        Assert.Equal(2f, created.Stats.AttackSpeed);
        Assert.Equal(1, created.Stats.Level);
        Assert.Equal(created.PlayerName, loaded.PlayerName);
        Assert.Equal(created.InteractionMode, loaded.InteractionMode);
        Assert.Equal(created.Stats, loaded.Stats);
        Assert.Equal(created.CreatedUtc, loaded.CreatedUtc);
    }

    [Fact]
    public void SqliteWorldPersistenceRepository_SavePlayerWorldState_RoundTripsLatestState()
    {
        using TempPersistenceDatabase database = new();
        using SqliteWorldPersistenceRepository repository = new(database.Schema);
        DateTime saveStartedUtc = DateTime.UtcNow;
        PersistedPlayerWorldState state = new(
            "player-world",
            new Vector3(12.5f, 4f, -8.25f),
            Vector3.Normalize(new Vector3(0.5f, 0f, -1f)),
            CurrentHealth: 71,
            CurrentEnergy: 18,
            UpdatedUtc: new DateTime(2026, 7, 4, 9, 0, 0, DateTimeKind.Utc));

        repository.SavePlayerWorldState(state);
        PersistedPlayerWorldState? loaded = repository.TryGetPlayerWorldState(" player-world ");
        DateTime saveFinishedUtc = DateTime.UtcNow;

        Assert.True(loaded.HasValue);
        Assert.Equal("player-world", loaded.Value.PlayerName);
        Assert.Equal(state.Position, loaded.Value.Position);
        Assert.Equal(state.Facing, loaded.Value.Facing);
        Assert.Equal(71, loaded.Value.CurrentHealth);
        Assert.Equal(18, loaded.Value.CurrentEnergy);
        Assert.InRange(loaded.Value.UpdatedUtc, saveStartedUtc, saveFinishedUtc);
    }

    [Fact]
    public void SqliteWorldPersistenceRepository_SavePlacedCube_RoundTripsAndDeletesCube()
    {
        using TempPersistenceDatabase database = new();
        using SqliteWorldPersistenceRepository repository = new(database.Schema);
        DateTime createdUtc = new(2026, 7, 4, 8, 15, 0, DateTimeKind.Utc);
        DateTime saveStartedUtc = DateTime.UtcNow;
        PersistedPlacedCube cube = new(
            CubeId: 41,
            OwnerPlayerName: "builder-a",
            CellPosition: new Vector3(6f, 2f, 9f),
            ShapeKind: GeometryShapeKind.SolidCube,
            MaterialId: GeometryMaterialId.Grass,
            CurrentHealth: 35,
            MaxHealth: 50,
            CreatedUtc: createdUtc,
            UpdatedUtc: createdUtc);

        repository.SavePlacedCube(cube);
        PersistedPlacedCube loaded = Assert.Single(repository.LoadPlacedCubes());

        Assert.Equal(41, loaded.CubeId);
        Assert.Equal("builder-a", loaded.OwnerPlayerName);
        Assert.Equal(cube.CellPosition, loaded.CellPosition);
        Assert.Equal(GeometryShapeKind.SolidCube, loaded.ShapeKind);
        Assert.Equal(GeometryMaterialId.Grass, loaded.MaterialId);
        Assert.Equal(35, loaded.CurrentHealth);
        Assert.Equal(50, loaded.MaxHealth);
        Assert.Equal(createdUtc, loaded.CreatedUtc);
        Assert.InRange(loaded.UpdatedUtc, saveStartedUtc, DateTime.UtcNow);

        repository.DeletePlacedCube(cube.CubeId);

        Assert.Empty(repository.LoadPlacedCubes());
    }

    [Fact]
    public void SqliteWorldPersistenceRepository_SaveEnemyInstance_RoundTripsAndDeletesEnemy()
    {
        using TempPersistenceDatabase database = new();
        using SqliteWorldPersistenceRepository repository = new(database.Schema);
        DateTime createdUtc = new(2026, 7, 4, 7, 45, 0, DateTimeKind.Utc);
        DateTime saveStartedUtc = DateTime.UtcNow;
        PersistedEnemyInstance enemy = new(
            EnemyId: 8,
            EnemyKind: "Wolf",
            DisplayName: "Ashfang",
            Position: new Vector3(-3f, 1.5f, 14f),
            Facing: Vector3.Normalize(new Vector3(1f, 0f, 1f)),
            Stats: new PersistedCreatureStats(
                CurrentHealth: 44,
                MaxHealth: 60,
                Strength: 13,
                Agility: 12,
                Vitality: 9,
                Energy: 5,
                MovementSpeed: 5.75f,
                AttackSpeed: 1.8f,
                Level: 3),
            CreatedUtc: createdUtc,
            UpdatedUtc: createdUtc);

        repository.SaveEnemyInstance(enemy);
        PersistedEnemyInstance loaded = Assert.Single(repository.LoadActiveEnemies());

        Assert.Equal(8, loaded.EnemyId);
        Assert.Equal("Wolf", loaded.EnemyKind);
        Assert.Equal("Ashfang", loaded.DisplayName);
        Assert.Equal(enemy.Position, loaded.Position);
        Assert.Equal(enemy.Facing, loaded.Facing);
        Assert.Equal(enemy.Stats, loaded.Stats);
        Assert.Equal(createdUtc, loaded.CreatedUtc);
        Assert.InRange(loaded.UpdatedUtc, saveStartedUtc, DateTime.UtcNow);

        repository.DeleteEnemyInstance(enemy.EnemyId);

        Assert.Empty(repository.LoadActiveEnemies());
    }

    [Fact]
    public void SqliteWorldPersistenceRepository_SaveItemAndInventorySlot_RoundTrips()
    {
        using TempPersistenceDatabase database = new();
        using SqliteWorldPersistenceRepository repository = new(database.Schema);
        DateTime createdUtc = new(2026, 7, 4, 11, 45, 0, DateTimeKind.Utc);
        PersistedItemInstance item = new(
            ItemInstanceId: 101,
            ItemKind: "tool.builder-hammer",
            DisplayName: "Builder Hammer",
            VisualId: "held.tool.builder",
            OwnerPlayerName: "player-one",
            WorldPosition: null,
            State: "Inventory",
            StackCount: 1,
            CreatedUtc: createdUtc,
            UpdatedUtc: createdUtc);

        repository.SaveItemInstance(item);
        repository.SaveInventorySlot(new PersistedInventorySlot("player-one", 0, 101, createdUtc));
        repository.SaveInventorySlot(new PersistedInventorySlot("player-one", 100, 101, createdUtc));

        PersistedItemInstance loadedItem = Assert.Single(repository.LoadItemInstances());
        Assert.Equal("Builder Hammer", loadedItem.DisplayName);
        PersistedInventorySlot[] slots = repository.LoadInventorySlots().OrderBy(slot => slot.SlotIndex).ToArray();
        Assert.Equal(2, slots.Length);
        Assert.Equal(0, slots[0].SlotIndex);
        Assert.Equal(100, slots[1].SlotIndex);

        repository.DeleteItemInstance(101);

        Assert.Empty(repository.LoadItemInstances());
    }

    private sealed class TempPersistenceDatabase : IDisposable
    {
        private const string SchemaScript = """
            PRAGMA foreign_keys = ON;

            CREATE TABLE IF NOT EXISTS player_profiles (
                player_name TEXT NOT NULL PRIMARY KEY,
                display_name TEXT NOT NULL,
                interaction_mode TEXT NOT NULL,
                strength INTEGER NOT NULL,
                agility INTEGER NOT NULL,
                vitality INTEGER NOT NULL,
                energy INTEGER NOT NULL,
                movement_speed REAL NOT NULL,
                attack_speed REAL NOT NULL,
                level INTEGER NOT NULL,
                current_hp INTEGER NOT NULL,
                max_hp INTEGER NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS player_world_state (
                player_name TEXT NOT NULL PRIMARY KEY,
                position_x REAL NOT NULL,
                position_y REAL NOT NULL,
                position_z REAL NOT NULL,
                facing_x REAL NOT NULL,
                facing_y REAL NOT NULL,
                facing_z REAL NOT NULL,
                current_hp INTEGER NOT NULL,
                current_energy INTEGER NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS placed_cube_instances (
                cube_id INTEGER NOT NULL PRIMARY KEY,
                owner_player_name TEXT NOT NULL,
                cell_x REAL NOT NULL,
                cell_y REAL NOT NULL,
                cell_z REAL NOT NULL,
                shape_kind TEXT NOT NULL,
                material_id TEXT NOT NULL,
                current_health INTEGER NOT NULL,
                max_health INTEGER NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS enemy_instances (
                enemy_id INTEGER NOT NULL PRIMARY KEY,
                enemy_kind TEXT NOT NULL,
                display_name TEXT NOT NULL,
                position_x REAL NOT NULL,
                position_y REAL NOT NULL,
                position_z REAL NOT NULL,
                facing_x REAL NOT NULL,
                facing_y REAL NOT NULL,
                facing_z REAL NOT NULL,
                current_hp INTEGER NOT NULL,
                max_hp INTEGER NOT NULL,
                strength INTEGER NOT NULL,
                agility INTEGER NOT NULL,
                vitality INTEGER NOT NULL,
                energy INTEGER NOT NULL,
                movement_speed REAL NOT NULL,
                attack_speed REAL NOT NULL,
                level INTEGER NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS item_instances (
                item_instance_id INTEGER NOT NULL PRIMARY KEY,
                item_kind TEXT NOT NULL,
                display_name TEXT NOT NULL,
                visual_id TEXT NOT NULL,
                owner_player_name TEXT NULL,
                world_position_x REAL NULL,
                world_position_y REAL NULL,
                world_position_z REAL NULL,
                state TEXT NOT NULL,
                stack_count INTEGER NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS player_inventory_slots (
                player_name TEXT NOT NULL,
                slot_index INTEGER NOT NULL,
                item_instance_id INTEGER NOT NULL,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY (player_name, slot_index)
            );
            """;

        private readonly string _rootDirectory;

        public TempPersistenceDatabase()
        {
            _rootDirectory = Path.Combine(Path.GetTempPath(), $"dream-persistence-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_rootDirectory);

            string databasePath = Path.Combine(_rootDirectory, "persistence.sqlite");
            string schemaPath = Path.Combine(_rootDirectory, "schema.sql");
            File.WriteAllText(schemaPath, SchemaScript);

            Schema = new SqliteSchemaBootstrapper(databasePath, schemaPath);
            Schema.EnsureSchema();
        }

        public SqliteSchemaBootstrapper Schema { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_rootDirectory, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (IOException)
            {
            }
        }
    }
}
