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

// The wolf's attack interval is an explicit, authored number (enemies.json attackIntervalSeconds, tunable
// per kind in enemy_kinds), so a spawn and a restore bite on exactly the same cadence bit for bit: the
// restore no longer derives it from the row's attack_speed. These bits pin that, and the damage and
// health the wolf's stats still derive.
public sealed class WolfIntervalBitsTest
{
    private static readonly uint WolfIntervalBits = BitConverter.SingleToUInt32Bits(1.8f);
    private static readonly uint WolfAttackSpeedBits = BitConverter.SingleToUInt32Bits(0.555556f); // 1 / 1.8, what a wolf row holds
    private const uint LegacyFormulaIntervalBits = 0x3F866666u; // 1.05f, the old derived interval

    private static readonly SceneEventHub Events = new();

    private static EnemyTemplate Wolf => ContentCatalog.Shipped.Wolf;

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);

    [Fact]
    public void TheShippedWolfAttackSpeed_IsTodaysStoredBits()
    {
        Assert.Equal(WolfAttackSpeedBits, Bits(Wolf.AttackSpeed));
        Assert.Equal(1.8f, Wolf.AttackIntervalSeconds);
        Assert.Equal(1.8f, 1f / Wolf.AttackSpeed, 4);
    }

    [Fact]
    public void AFreshlySpawnedWolf_HasItsAuthoredIntervalAndTheDerivedDamageAndHealth()
    {
        EnemyActor wolf = EnemyFactory.Spawn(Wolf, new Vector3(5.5f, 1f, 2.5f), 2000, Events, CombatFormulaService.Default);

        Assert.Equal(WolfIntervalBits, Bits(wolf.Combatant.AttackIntervalSeconds));
        Assert.Equal(12, wolf.Combatant.AttackDamage);
        Assert.Equal(100, wolf.Combatant.Health.MaxHealth);
        Assert.Equal(100, wolf.Combatant.Health.CurrentHealth);
        Assert.Equal(1, wolf.Level);
        Assert.Null(wolf.VisualKey);
        Assert.Equal(Wolf.MoveSpeed, wolf.MoveSpeed);
        Assert.Equal(Wolf.PatrolSpeed, wolf.PatrolSpeed);
    }

    [Fact]
    public void ARestoredWolfRow_BitesAtItsKindsInterval_WhateverItsRowSays_AndKeepsDamageAndHealth()
    {
        // A legacy row: attack speed 1f / 1.05f, stored as a REAL. The interval is a kind stat, so the row's
        // attack speed no longer sets it.
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
        Assert.Equal(LegacyFormulaIntervalBits, Bits(old));
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
