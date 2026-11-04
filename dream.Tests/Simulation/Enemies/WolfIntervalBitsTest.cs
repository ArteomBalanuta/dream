using OpenTK.Mathematics;
using Xunit;
using Dream.Server.Persistence;
using Dream.Simulation.Content;
using Dream.Simulation.Enemies;
using Dream.Simulation.Persistence;
using Dream.Simulation.Rules;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Simulation.Enemies;

// Wave I C0b gate: the wolf's attack interval stays today's 1.05f bit for bit, now that it comes from
// the stored attack speed through enemy.attack_interval_seconds instead of the template's number.
// The shipped wolf pins no explicit interval (I-D5), so these bits are the only thing keeping wolves
// attacking on exactly the old cadence, on this runtime.
public sealed class WolfIntervalBitsTest
{
    private const uint WolfIntervalBits = 0x3F866666u; // 1.05f
    private const uint WolfAttackSpeedBits = 0x3F73CF3Eu; // 1f / 1.05f, what every stored wolf row holds

    private static readonly SceneEventHub Events = new();

    private static EnemyTemplate Wolf => ContentCatalog.Shipped.Wolf;

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);

    [Fact]
    public void TheShippedWolfAttackSpeed_IsTodaysStoredBits()
    {
        Assert.Equal(WolfAttackSpeedBits, Bits(Wolf.AttackSpeed));
        Assert.Equal(WolfAttackSpeedBits, Bits(1f / 1.05f));
        Assert.Null(Wolf.AttackIntervalSeconds);
    }

    [Fact]
    public void AFreshlySpawnedWolf_HasTheOldIntervalDamageAndHealth()
    {
        EnemyActor wolf = EnemyFactory.Spawn(Wolf, new Vector3(5.5f, 1f, 2.5f), 2000, Events, CombatFormulaService.Default);

        Assert.Equal(WolfIntervalBits, Bits(wolf.Combatant.AttackIntervalSeconds));
        Assert.Equal(12, wolf.Combatant.AttackDamage);
        Assert.Equal(100, wolf.Combatant.Health.MaxHealth);
        Assert.Equal(100, wolf.Combatant.Health.CurrentHealth);
        Assert.Equal(1, wolf.Level);
        Assert.Null(wolf.VisualKey);
        // The chase speed was re-based from 2.8 to 5.0 this wave (the player runs at 4.3 and sprints
        // at 6.0, so at 2.8 no wolf could ever close). The interval, damage and health bits above are
        // what this test exists to pin, and none of them moved with it.
        Assert.Equal(5.0f, wolf.MoveSpeed);
        Assert.Equal(1.4f, wolf.PatrolSpeed);
    }

    [Fact]
    public void ARestoredWolfRow_HasTheOldIntervalDamageAndHealth()
    {
        // The row a wolf has always been written with: attack speed 1f / 1.05f, stored as a REAL.
        double stored = 1f / 1.05f;
        PersistedEnemyInstance row = Row(strength: 10, vitality: 10, attackSpeed: (float)stored, currentHealth: 37, maxHealth: 100);

        EnemyActor wolf = EnemyFactory.Restore(row, Wolf, 2001, Events, CombatFormulaService.Default);

        Assert.Equal(WolfIntervalBits, Bits(wolf.Combatant.AttackIntervalSeconds));
        Assert.Equal(12, wolf.Combatant.AttackDamage);
        Assert.Equal(100, wolf.Combatant.Health.MaxHealth);
        Assert.Equal(37, wolf.Combatant.Health.CurrentHealth);
    }

    [Fact]
    public void TheIntervalFormula_GivesTheSameBitsAsTodaysFloatDivision_ForTheStoredRow()
    {
        float speed = 1f / 1.05f;
        float old = Math.Max(0.05f, 1f / Math.Max(0.1f, speed));

        EnemyCombatDerivedStats derived = CombatFormulaService.Default.EvaluateEnemyDerivedStats(10, speed, 10, 1, null);

        Assert.Equal(Bits(old), Bits(derived.AttackIntervalSeconds));
        Assert.Equal(WolfIntervalBits, Bits(old));
    }

    // A real repository: the spawned wolf's row is written, read back and restored by a second runtime.
    [Fact]
    public void ASpawnedWolf_ThroughARealDatabase_RestoresWithTheSameBitsAndTheSameRow()
    {
        using Dream.Tests.Server.Persistence.IncrementalCheckpointTests.TempDatabase database = new();
        SqliteWorldPersistenceRepository world = new(database.Schema);
        PersistedEnemyInstance written;
        uint spawnedBits;

        using (WorldSessionRuntime first = CreateRuntime(database, world))
        {
            EnemyActor spawned = Assert.Single(first.Enemies.ActiveActors);
            spawnedBits = Bits(spawned.Combatant.AttackIntervalSeconds);
            Assert.True(first.PersistenceWrites!.Flush(TimeSpan.FromSeconds(10)));
            written = Assert.Single(world.LoadActiveEnemies());
        }

        Assert.Equal(WolfIntervalBits, spawnedBits);
        Assert.Equal(10, written.Stats.Strength);
        Assert.Equal(10, written.Stats.Agility);
        Assert.Equal(10, written.Stats.Vitality);
        Assert.Equal(0, written.Stats.Energy);
        Assert.Equal(WolfAttackSpeedBits, Bits(written.Stats.AttackSpeed));
        Assert.Equal(100, written.Stats.MaxHealth);

        using (WorldSessionRuntime second = CreateRuntime(database, world))
        {
            EnemyActor restored = Assert.Single(second.Enemies.ActiveActors);
            Assert.Equal(WolfIntervalBits, Bits(restored.Combatant.AttackIntervalSeconds));
            Assert.Equal(12, restored.Combatant.AttackDamage);
            second.PersistCheckpoint();
            Assert.True(second.PersistenceWrites!.Flush(TimeSpan.FromSeconds(10)));
        }

        PersistedEnemyInstance after = Assert.Single(world.LoadActiveEnemies());
        Assert.Equal(written.Stats, after.Stats);
        Assert.Equal(written.ObjectId, after.ObjectId);
    }

    private static WorldSessionRuntime CreateRuntime(Dream.Tests.Server.Persistence.IncrementalCheckpointTests.TempDatabase database, IWorldPersistenceRepository world)
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

    internal static PersistedEnemyInstance Row(int strength, int vitality, float attackSpeed, int currentHealth, int maxHealth, string kind = "Wolf", int level = 1) =>
        new(
            new ObjectId(2000),
            kind,
            kind,
            new Vector3(9f, 1f, 9f),
            Vector3.UnitZ,
            new PersistedCreatureStats(currentHealth, maxHealth, strength, 10, vitality, 0, 2.8f, attackSpeed, level),
            DateTime.UtcNow,
            DateTime.UtcNow);
}
