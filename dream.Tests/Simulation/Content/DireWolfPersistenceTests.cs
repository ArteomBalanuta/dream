using OpenTK.Mathematics;
using Xunit;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Server.Persistence.Worlds;
using Dream.Simulation.Content;
using Dream.Simulation.Enemies;
using Dream.Simulation.Persistence;
using Dream.Simulation.Rules;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Server.Persistence;

namespace Dream.Tests.Simulation.Content;

// Wave I1 (I-D4, I-D6, I-D21): a Dire Wolf is an instance with its own persisted stats. A restart, a
// world copy and a template edit keep what the row says; only the next spawn sees an edit.
public sealed class DireWolfPersistenceTests
{
    private static readonly PersistedCreatureStats DireStats = new(150, 200, 16, 10, 30, 0, 3.2f, 1.0f, 3);

    [Fact]
    public void ALiveDire_IsSpawnedWithItsTemplateStats_AndPersistsThem()
    {
        using IncrementalCheckpointTests.TempDatabase database = new();
        SqliteWorldPersistenceRepository world = new(database.Schema);

        using (WorldSessionRuntime runtime = CreateRuntime(database, world))
        {
            runtime.ConfigureWorldClock(WorldClockSettings.Default, NightWorldTicks());
            runtime.AdvanceAuthoritativeTick(
                1,
                SimulationClock.FixedDeltaSeconds,
                default(Dream.Simulation.Players.PlayerCommandFrame),
                Dream.Simulation.Players.PlayerSimulationTuning.AuthoritativeDefault);
            Assert.Equal(["Wolf", "DireWolf"], runtime.Enemies.ActiveActors.Select(actor => actor.EnemyKind).ToArray());
            runtime.PersistCheckpoint();
            Assert.True(runtime.PersistenceWrites!.Flush(TimeSpan.FromSeconds(10)));
        }

        IReadOnlyList<PersistedEnemyInstance> rows = world.LoadActiveEnemies();
        PersistedEnemyInstance dire = Assert.Single(rows, row => row.EnemyKind == "DireWolf");
        Assert.Equal(16, dire.Stats.Strength);
        Assert.Equal(30, dire.Stats.Vitality);
        Assert.Equal(1.0f, dire.Stats.AttackSpeed);
        Assert.Equal(3, dire.Stats.Level);
        Assert.Equal(200, dire.Stats.MaxHealth);
        Assert.Equal(200, dire.Stats.CurrentHealth);
        Assert.Equal(10, dire.Stats.Agility);
        Assert.Equal(0, dire.Stats.Energy);
        Assert.Equal("Dire Wolf", dire.DisplayName);

        // A restart restores both from their rows, with their stats and no third enemy.
        using WorldSessionRuntime restored = CreateRuntime(database, world);
        Assert.Equal(["Wolf", "DireWolf"], restored.Enemies.ActiveActors.Select(actor => actor.EnemyKind).ToArray());
        EnemyActor restoredDire = restored.Enemies.ActiveActors[1];
        Assert.Equal(18, restoredDire.Combatant.AttackDamage);
        Assert.Equal(200, restoredDire.Combatant.Health.MaxHealth);
        Assert.Equal(1f, restoredDire.Combatant.AttackIntervalSeconds);
        Assert.Equal(3, restoredDire.Level);
        Assert.Equal("enemy.dire-wolf", restoredDire.VisualKey);
    }

    [Fact]
    public void ARestoredDire_ReadsItsRowsStats_NotTheTemplates()
    {
        using IncrementalCheckpointTests.TempDatabase database = new();
        SqliteWorldPersistenceRepository world = new(database.Schema);
        DateTime now = DateTime.UtcNow;
        // A Dire saved under an older balance: Strength 20 (damage 22), Vitality 40 (250 HP), half hurt.
        world.SaveEnemyInstance(Row(3001, DireStats with { Strength = 20, Vitality = 40, MaxHealth = 250, CurrentHealth = 125 }, now));

        using WorldSessionRuntime runtime = CreateRuntime(database, world);

        EnemyActor dire = Assert.Single(runtime.Enemies.ActiveActors);
        Assert.Equal("DireWolf", dire.EnemyKind);
        Assert.Equal(22, dire.Combatant.AttackDamage);
        Assert.Equal(250, dire.Combatant.Health.MaxHealth);
        Assert.Equal(125, dire.Combatant.Health.CurrentHealth);
        Assert.Equal(3, dire.Level);
        // The template still supplies aggro, range and the visual.
        Assert.Equal(12f, dire.AggroRange);
        Assert.Equal(1.35f, dire.AttackRange);
        Assert.Equal("enemy.dire-wolf", dire.VisualKey);
    }

    [Fact]
    public void EditingTheTemplate_LeavesARestoredDireAlone_AndChangesTheNextSpawn()
    {
        ContentCatalog.Shipped.TryGetEnemy("DireWolf", out EnemyTemplate shipped);
        EnemyTemplate edited = shipped with { Strength = 30, Vitality = 50 };
        CombatFormulaService formulas = CombatFormulaService.Default;
        SceneEventHub events = new();
        PersistedEnemyInstance row = Row(3002, DireStats, DateTime.UtcNow);

        EnemyActor restored = EnemyFactory.Restore(row, edited, actorId: 2100, events, formulas);
        EnemyActor spawnedOld = EnemyFactory.Spawn(shipped, new Vector3(5.5f, 1f, 2.5f), actorId: 2101, events, formulas);
        EnemyActor spawnedNew = EnemyFactory.Spawn(edited, new Vector3(5.5f, 1f, 2.5f), actorId: 2102, events, formulas);

        Assert.Equal(18, restored.Combatant.AttackDamage);
        Assert.Equal(200, restored.Combatant.Health.MaxHealth);
        Assert.Equal(150, restored.Combatant.Health.CurrentHealth);
        Assert.Equal(18, spawnedOld.Combatant.AttackDamage);
        Assert.Equal(200, spawnedOld.Combatant.Health.MaxHealth);
        Assert.Equal(32, spawnedNew.Combatant.AttackDamage);
        Assert.Equal(300, spawnedNew.Combatant.Health.MaxHealth);
    }

    [Fact]
    public void AWorldCopy_CarriesTheDireRowWithItsStats()
    {
        using IncrementalCheckpointTests.TempDatabase database = new();
        SqliteWorldPersistenceRepository world = new(database.Schema);
        world.SaveEnemyInstance(Row(3003, DireStats, DateTime.UtcNow));
        SqliteWorldCatalog catalog = new(database.Schema);

        Dream.Server.Worlds.WorldRecord copy = catalog.Copy(WorldId.Default, "twin");

        SqliteWorldPersistenceRepository copied = new(database.Schema, copy.Descriptor.Id);
        PersistedEnemyInstance row = Assert.Single(copied.LoadActiveEnemies());
        Assert.Equal("DireWolf", row.EnemyKind);
        Assert.Equal(DireStats, row.Stats);
        Assert.Equal(Assert.Single(world.LoadActiveEnemies()).Stats, row.Stats);
    }

    [Fact]
    public void ARetiredDireKey_IsSkippedWithItsRowKept_CountedByTheIntegrityCheck_AndAWolfFillsTheSlot()
    {
        using IncrementalCheckpointTests.TempDatabase database = new();
        SqliteWorldPersistenceRepository world = new(database.Schema);
        // "Retired" is a key the catalog does not hold; the shipped catalog holds DireWolf, so the
        // row's kind is one it dropped.
        PersistedEnemyInstance retired = Row(3004, DireStats, DateTime.UtcNow) with { EnemyKind = "DireWolfRetired", DisplayName = "Dire Wolf" };
        world.SaveEnemyInstance(retired);

        using (WorldSessionRuntime runtime = CreateRuntime(database, world))
        {
            Assert.Empty(runtime.Enemies.ActiveActors);
            runtime.AdvanceAuthoritativeTick(
                1,
                SimulationClock.FixedDeltaSeconds,
                default(Dream.Simulation.Players.PlayerCommandFrame),
                Dream.Simulation.Players.PlayerSimulationTuning.AuthoritativeDefault);
            EnemyActor filler = Assert.Single(runtime.Enemies.ActiveActors);
            Assert.Equal("Wolf", filler.EnemyKind);
            runtime.PersistCheckpoint();
            Assert.True(runtime.PersistenceWrites!.Flush(TimeSpan.FromSeconds(10)));
        }

        Assert.Contains(world.LoadActiveEnemies(), row => row.ObjectId == new ObjectId(3004) && row.EnemyKind == "DireWolfRetired");

        // The same applies to a real retirement: a catalog without DireWolf counts the shipped Dire row.
        world.SaveEnemyInstance(Row(3005, DireStats, DateTime.UtcNow));
        ContentCatalog wolfOnly = ContentCatalog.Load(
            File.ReadAllText(Path.Combine(ContentCatalog.ShippedDirectory, ContentCatalog.ItemsFileName)),
            """
            {"version":2,"enemies":[{"key":"Wolf","displayName":"Wolf","level":1,"strength":10,"vitality":10,
             "attackSpeed":0.952381,"aggroRange":10,"attackRange":1.35,"moveSpeed":2.8,
             "visualKey":"enemy.wolf","spawnRole":"Primary"}]}
            """,
            File.ReadAllText(Path.Combine(ContentCatalog.ShippedDirectory, ContentCatalog.LootFileName)));
        using Microsoft.Data.Sqlite.SqliteConnection connection = database.Schema.CreateOpenConnection();
        WorldIntegrityReport report = WorldIntegrityCheck.Run(connection, WorldDescriptor.Default, ServerBootstrap.IntegrityOptionsFor(wolfOnly));
        Assert.Equal(2, report.UnknownEnemyKinds);
        Assert.False(report.IsFatal);
        WorldIntegrityReport shipped = WorldIntegrityCheck.Run(connection, WorldDescriptor.Default, ServerBootstrap.IntegrityOptionsFor(ContentCatalog.Shipped));
        Assert.Equal(1, shipped.UnknownEnemyKinds);
    }

    private static long NightWorldTicks()
    {
        WorldClockSettings settings = WorldClockSettings.Default;
        return (long)Math.Round(23 / 24d * settings.TicksPerDay) - settings.StartOffsetTicks;
    }

    private static WorldSessionRuntime CreateRuntime(IncrementalCheckpointTests.TempDatabase database, IWorldPersistenceRepository world)
    {
        WorldSessionRuntime runtime = new(
            20,
            new Vector3(5f, 1.5f, 5f),
            new SceneEventHub(),
            playerProfiles: new SqlitePlayerProfileRepository(database.Schema),
            worldPersistence: world,
            persistenceWrites: PersistenceWriteQueue.Background());
        runtime.WriteCheckpointOnDispose = false;
        return runtime;
    }

    private static PersistedEnemyInstance Row(int id, PersistedCreatureStats stats, DateTime now)
    {
        return new PersistedEnemyInstance(
            new ObjectId(id),
            "DireWolf",
            "Dire Wolf",
            new Vector3(9f + (id % 3), 1f, 9f),
            Vector3.UnitZ,
            stats,
            now,
            now);
    }
}
