using Dream.Simulation.World;
using Dream.Server.Persistence;
using Dream.Simulation.Persistence;
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
                Level: 5,
                Experience: 222),
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
        Assert.Equal(222, loaded.Value.Stats.Experience);
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
        Assert.Equal(0, created.Stats.Experience);
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
        database.AddProfile("player-world");
        PersistedPlayerWorldState state = new(
            "player-world",
            new Vector3(12.5f, 4f, -8.25f),
            Vector3.Normalize(new Vector3(0.5f, 0f, -1f)),
            CurrentHealth: 71,
            CurrentEnergy: 18,
            UpdatedUtc: new DateTime(2026, 7, 4, 9, 0, 0, DateTimeKind.Utc));

        repository.SavePlayerWorldState(state);
        PersistedPlayerWorldState? loaded = repository.TryGetPlayerWorldState(" player-world ");

        Assert.True(loaded.HasValue);
        Assert.Equal("player-world", loaded.Value.PlayerName);
        Assert.Equal(state.Position, loaded.Value.Position);
        Assert.Equal(state.Facing, loaded.Value.Facing);
        Assert.Equal(71, loaded.Value.CurrentHealth);
        Assert.Equal(18, loaded.Value.CurrentEnergy);
        // The state's own UpdatedUtc (its capture time), not the write time.
        Assert.Equal(state.UpdatedUtc, loaded.Value.UpdatedUtc);
    }

    [Fact]
    public void SqliteWorldPersistenceRepository_SavePlacedCube_RoundTripsAndDeletesCube()
    {
        using TempPersistenceDatabase database = new();
        using SqliteWorldPersistenceRepository repository = new(database.Schema);
        database.AddProfile("builder-a");
        DateTime createdUtc = new(2026, 7, 4, 8, 15, 0, DateTimeKind.Utc);
        DateTime saveStartedUtc = DateTime.UtcNow;
        PersistedPlacedCube cube = new(
            ObjectId: new ObjectId(41),
            OwnerPlayerName: "builder-a",
            CellPosition: new Vector3(6f, 2f, 9f),
            ShapeKind: GeometryShapeKind.SolidCube,
            MaterialId: GeometryMaterialId.Grass,
            CurrentHealth: 35,
            MaxHealth: 50,
            CreatedUtc: createdUtc,
            UpdatedUtc: createdUtc,
            CenterPosition: new Vector3(6.72f, 2.5f, 9.18f));

        repository.SavePlacedCube(cube);
        PersistedPlacedCube loaded = Assert.Single(repository.LoadPlacedCubes());

        Assert.Equal(41, (int)loaded.ObjectId.Value);
        Assert.Equal("builder-a", loaded.OwnerPlayerName);
        Assert.Equal(cube.CellPosition, loaded.CellPosition);
        Assert.Equal(cube.CenterPosition, loaded.CenterPosition);
        Assert.Equal(GeometryShapeKind.SolidCube, loaded.ShapeKind);
        Assert.Equal(GeometryMaterialId.Grass, loaded.MaterialId);
        Assert.Equal(35, loaded.CurrentHealth);
        Assert.Equal(50, loaded.MaxHealth);
        Assert.Equal(createdUtc, loaded.CreatedUtc);
        Assert.InRange(loaded.UpdatedUtc, saveStartedUtc, DateTime.UtcNow);

        repository.DeletePlacedCube(new ObjectId((int)cube.ObjectId.Value));

        Assert.Empty(repository.LoadPlacedCubes());
    }

    [Fact]
    public void SqliteWorldPersistenceRepository_SaveCubeFracturePiece_RoundTripsAndDeletesPiecesForSourceCube()
    {
        using TempPersistenceDatabase database = new();
        using SqliteWorldPersistenceRepository repository = new(database.Schema);
        database.AddProfile("builder-a");
        DateTime createdUtc = new(2026, 7, 4, 8, 30, 0, DateTimeKind.Utc);
        DateTime saveStartedUtc = DateTime.UtcNow;
        PersistedCubeFracturePiece first = new(
            ObjectId: new ObjectId(501),
            OriginObjectId: new ObjectId(41),
            PieceIndex: 0,
            OwnerPlayerName: "builder-a",
            ParentCell: new Vector3(6f, 2f, 9f),
            Center: new Vector3(6.18f, 2.22f, 9.33f),
            SideLength: 0.2f,
            MaterialId: GeometryMaterialId.Stone,
            CreatedUtc: createdUtc,
            UpdatedUtc: createdUtc);
        PersistedCubeFracturePiece second = first with
        {
            ObjectId = new ObjectId(502),
            PieceIndex = 1,
            Center = new Vector3(6.72f, 2.17f, 9.64f)
        };

        repository.SaveCubeFracturePiece(first);
        repository.SaveCubeFracturePiece(second);
        PersistedCubeFracturePiece[] loaded = repository.LoadCubeFracturePieces().OrderBy(piece => piece.PieceIndex).ToArray();

        Assert.Equal(2, loaded.Length);
        Assert.Equal(41, (int)loaded[0].OriginObjectId.Value);
        Assert.Equal(0, loaded[0].PieceIndex);
        Assert.Equal("builder-a", loaded[0].OwnerPlayerName);
        Assert.Equal(first.ParentCell, loaded[0].ParentCell);
        Assert.Equal(first.Center, loaded[0].Center);
        Assert.Equal(0.2f, loaded[0].SideLength);
        Assert.Equal(GeometryMaterialId.Stone, loaded[0].MaterialId);
        Assert.Equal(createdUtc, loaded[0].CreatedUtc);
        Assert.InRange(loaded[0].UpdatedUtc, saveStartedUtc, DateTime.UtcNow);
        Assert.Equal(second.Center, loaded[1].Center);

        repository.DeleteCubeFracturePieces(new ObjectId(41));

        Assert.Empty(repository.LoadCubeFracturePieces());
    }

    [Fact]
    public void SqliteWorldPersistenceRepository_SaveEnemyInstance_RoundTripsAndDeletesEnemy()
    {
        using TempPersistenceDatabase database = new();
        using SqliteWorldPersistenceRepository repository = new(database.Schema);
        DateTime createdUtc = new(2026, 7, 4, 7, 45, 0, DateTimeKind.Utc);
        DateTime saveStartedUtc = DateTime.UtcNow;
        PersistedEnemyInstance enemy = new(
            ObjectId: new ObjectId(8),
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

        Assert.Equal(8, (int)loaded.ObjectId.Value);
        Assert.Equal("Wolf", loaded.EnemyKind);
        Assert.Equal("Ashfang", loaded.DisplayName);
        Assert.Equal(enemy.Position, loaded.Position);
        Assert.Equal(enemy.Facing, loaded.Facing);
        Assert.Equal(enemy.Stats, loaded.Stats);
        Assert.Equal(createdUtc, loaded.CreatedUtc);
        Assert.InRange(loaded.UpdatedUtc, saveStartedUtc, DateTime.UtcNow);

        repository.DeleteEnemyInstance(new ObjectId((int)enemy.ObjectId.Value));

        Assert.Empty(repository.LoadActiveEnemies());
    }

    [Fact]
    public void SqliteWorldPersistenceRepository_SaveItemAndInventorySlot_RoundTrips()
    {
        using TempPersistenceDatabase database = new();
        using SqliteWorldPersistenceRepository repository = new(database.Schema);
        database.AddProfile("player-one");
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

    [Fact]
    public void SqliteCombatFormulaRepository_LoadAllEnabled_ReturnsSeededPlayerFormulas()
    {
        using TempPersistenceDatabase database = new();
        using SqliteCombatFormulaRepository repository = new(database.Schema);

        CombatFormulaDefinition[] formulas = repository.LoadAllEnabled()
            .OrderBy(definition => definition.FormulaKey, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(8, formulas.Length);
        Assert.Contains(formulas, definition =>
            definition.FormulaKey == "player.min_damage" &&
            definition.ExpressionText == "5 + strength * 2" &&
            definition.Enabled);
        Assert.Contains(formulas, definition =>
            definition.FormulaKey == "player.attack_interval_seconds" &&
            definition.ExpressionText == "1.0 / attack_speed" &&
            definition.Enabled);
        Assert.Contains(formulas, definition =>
            definition.FormulaKey == "player.experience_to_next_level" &&
            definition.ExpressionText == "1000 + level * 23");
        Assert.Contains(formulas, definition =>
            definition.FormulaKey == "enemy.experience_reward" &&
            definition.ExpressionText == "enemy_level * 14");
    }

    private sealed class TempPersistenceDatabase : IDisposable
    {
        private readonly string _rootDirectory;

        public TempPersistenceDatabase()
        {
            _rootDirectory = Path.Combine(Path.GetTempPath(), $"dream-persistence-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_rootDirectory);

            string databasePath = Path.Combine(_rootDirectory, "persistence.sqlite");
            // The shipped schema script and migrations (config/migrations is found next to it), so the
            // tests run on the schema the server runs on.
            string schemaPath = Path.Combine(Dream.Tests.Integration.RepoRoot.Find(), "dream", "config", "player-profiles.init.sql");
            Schema = new SqliteSchemaBootstrapper(databasePath, schemaPath);
            Schema.EnsureSchema();
        }

        public SqliteSchemaBootstrapper Schema { get; }

        // The shipped schema enforces the owner foreign keys, so a row's owner needs a profile.
        public void AddProfile(string playerName) =>
            new SqlitePlayerProfileRepository(Schema).Save(PersistenceGroupCommitTests.Profile(playerName, experience: 0));

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
