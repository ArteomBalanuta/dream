using OpenTK.Mathematics;
using Xunit;
using Dream.Simulation.Combat;
using Dream.Simulation.Content;
using Dream.Simulation.Enemies;
using Dream.Simulation.Persistence;
using Dream.Simulation.Rules;
using Dream.Simulation.Runtime;
using Dream.Simulation.Stats;
using Dream.Simulation.World;

namespace Dream.Tests.Simulation.Enemies;

// Wave I C0b: EnemyFactory builds an enemy from a template (spawn) or a row (restore) with its
// stats on a StatSheet and damage, interval and health derived through the enemy.* formulas.
public sealed class EnemyFactoryTests
{
    private static readonly SceneEventHub Events = new();

    private static EnemyTemplate Dire(float? interval = null) => new(
        "DireWolf", "Dire Wolf", Level: 3, Strength: 16, Vitality: 30, AttackSpeed: 1.0f, interval,
        AggroRange: 12f, AttackRange: 1.35f, MoveSpeed: 3.2f, "enemy.dire-wolf", "dire-wolf", EnemySpawnRole.NightExtra);

    [Fact]
    public void ADireSpawn_DerivesDamageHealthAndInterval_FromItsTemplateStats()
    {
        EnemyActor dire = EnemyFactory.Spawn(Dire(), new Vector3(1f, 1f, 1f), 2100, Events, CombatFormulaService.Default);

        Assert.Equal(18, dire.Combatant.AttackDamage);
        Assert.Equal(200, dire.Combatant.Health.MaxHealth);
        Assert.Equal(1.0f, dire.Combatant.AttackIntervalSeconds);
        Assert.Equal(3, dire.Level);
        Assert.Equal("enemy.dire-wolf", dire.VisualKey);
        Assert.Equal("DireWolf", dire.EnemyKind);
        Assert.Equal(12f, dire.AggroRange);
        Assert.Equal(3.2f, dire.MoveSpeed);

        ref readonly StatBlock stats = ref dire.Combatant.StatSheet.Base;
        Assert.Equal(16f, stats[StatId.Strength]);
        Assert.Equal(EnemyFactory.DefaultAgility, (int)stats[StatId.Agility]);
        Assert.Equal(30f, stats[StatId.Vitality]);
        Assert.Equal(EnemyFactory.DefaultEnergy, (int)stats[StatId.Energy]);
        Assert.Equal(1.0f, stats[StatId.AttackSpeed]);
    }

    [Fact]
    public void AnExplicitTemplateInterval_ReplacesTheFormula_OnSpawnOnly()
    {
        EnemyActor spawned = EnemyFactory.Spawn(Dire(interval: 2.5f), Vector3.Zero, 2100, Events, CombatFormulaService.Default);
        PersistedEnemyInstance row = WolfIntervalBitsTest.Row(16, 30, 1.0f, 200, 200, "DireWolf", 3);
        EnemyActor restored = EnemyFactory.Restore(row, Dire(interval: 2.5f), 2101, Events, CombatFormulaService.Default);

        Assert.Equal(2.5f, spawned.Combatant.AttackIntervalSeconds);
        Assert.Equal(1.0f, restored.Combatant.AttackIntervalSeconds);
    }

    [Fact]
    public void ARestore_ReadsStatsAndHealthFromTheRow_NotTheTemplate()
    {
        // A Dire row stored under an older balance: the template now says 16 / 30 but the row keeps its own.
        PersistedEnemyInstance row = WolfIntervalBitsTest.Row(strength: 20, vitality: 40, attackSpeed: 2.0f, currentHealth: 90, maxHealth: 250, "DireWolf", level: 3);

        EnemyActor restored = EnemyFactory.Restore(row, Dire(), 2100, Events, CombatFormulaService.Default);

        Assert.Equal(22, restored.Combatant.AttackDamage);
        Assert.Equal(0.5f, restored.Combatant.AttackIntervalSeconds);
        Assert.Equal(250, restored.Combatant.Health.MaxHealth);
        Assert.Equal(90, restored.Combatant.Health.CurrentHealth);
        Assert.Equal(3, restored.Level);
        Assert.Equal("DireWolf", restored.EnemyKind);
        Assert.Equal("enemy.dire-wolf", restored.VisualKey);
        Assert.Equal(row.Facing, restored.Forward);
        Assert.Equal(row.Position, restored.Position);
    }

    [Fact]
    public void ARestore_OfAHostileRow_ClampsItsStats_AndNeverThrows()
    {
        PersistedEnemyInstance row = WolfIntervalBitsTest.Row(strength: int.MaxValue, vitality: -5, attackSpeed: float.NaN, currentHealth: 5, maxHealth: 10);

        EnemyActor restored = EnemyFactory.Restore(row, ContentCatalog.Shipped.Wolf, 2100, Events, CombatFormulaService.Default);

        Assert.Equal(50f, restored.Combatant.StatSheet.Base[StatId.Strength]);
        Assert.Equal(1f, restored.Combatant.StatSheet.Base[StatId.Vitality]);
        Assert.Equal(0.5f, restored.Combatant.StatSheet.Base[StatId.AttackSpeed]);
        Assert.Equal(52, restored.Combatant.AttackDamage);
        Assert.Equal(2.0f, restored.Combatant.AttackIntervalSeconds);
        Assert.Equal(10, restored.Combatant.Health.MaxHealth);
    }

    [Fact]
    public void TheDefaultVisual_IsNullOnTheActor_AndAScriptedEnemyKeepsTodaysRowStats()
    {
        EnemyActor wolf = EnemyFactory.Spawn(ContentCatalog.Shipped.Wolf, Vector3.Zero, 2000, Events, CombatFormulaService.Default);
        EnemyCombatant scripted = new(() => Vector3.Zero, maxHealth: 30, attackDamage: 6, attackIntervalSeconds: 1f);

        Assert.Null(wolf.VisualKey);
        Assert.Equal(10f, scripted.StatSheet.Base[StatId.Strength]);
        Assert.Equal(10f, scripted.StatSheet.Base[StatId.Agility]);
        Assert.Equal(10f, scripted.StatSheet.Base[StatId.Vitality]);
        Assert.Equal(0f, scripted.StatSheet.Base[StatId.Energy]);
        Assert.Equal(1f, scripted.StatSheet.Base[StatId.AttackSpeed]);
    }

    [Fact]
    public void AWolfOnly_RuntimeSnapshot_CarriesNoEnemyPresentation()
    {
        using WorldSessionRuntime runtime = new(20, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        EnemyActor wolf = Assert.Single(runtime.Enemies.ActiveActors);
        ActorSnapshot snapshot = runtime.CaptureActorSnapshots().Single(actor => actor.Kind == CreatureKind.Enemy);

        Assert.Equal(wolf.Combatant.Actor.Id, snapshot.ActorId);
        Assert.Equal(0, snapshot.EnemyLevel);
        Assert.Null(snapshot.VisualKey);
        Assert.Equal(ActorFieldGroups.All, ActorWireRecord.Full(snapshot).Groups);
    }
}
