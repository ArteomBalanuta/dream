using OpenTK.Mathematics;
using Dream.Simulation.Combat;
using Dream.Simulation.Content;
using Dream.Simulation.Enemies;
using Dream.Simulation.Persistence;
using Dream.Simulation.Rules;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-SIM-2: EnemyFactory.Restore clamps the level and every StatId on a persisted row, but took
// max_health from the row raw, so a tampered or corrupt row restored an enemy nobody could kill.
// The health pool is now clamped against the same ceiling the enemy sheet is validated against
// (ContentCatalog.MaxEnemyMaxHealth), and current health cannot exceed it.
public sealed class SimEnemyRestoreHealthTests
{
    private static readonly SceneEventHub Events = new();

    private static EnemyTemplate Wolf() => new(
        "Wolf", "Wolf", Level: 1, Strength: 8, Vitality: 10, AttackSpeed: 1.0f, null,
        AggroRange: 12f, AttackRange: 1.35f, MoveSpeed: 3.2f, ContentCatalog.DefaultEnemyVisualKey, "wolf", EnemySpawnRole.NightExtra);

    private static PersistedEnemyInstance Row(int currentHealth, int maxHealth) => new(
        new ObjectId(2000),
        "Wolf",
        "Wolf",
        new Vector3(9f, 1f, 9f),
        Vector3.UnitZ,
        new PersistedCreatureStats(currentHealth, maxHealth, 8, 10, 10, 0, 2.8f, 1.0f, 1),
        DateTime.UnixEpoch,
        DateTime.UnixEpoch);

    [Fact]
    public void ARestore_OfARowAboveTheContentHealthCeiling_ClampsMaxAndCurrentHealth()
    {
        EnemyActor restored = EnemyFactory.Restore(
            Row(currentHealth: 2_000_000_000, maxHealth: 2_000_000_000),
            Wolf(),
            2100,
            Events,
            CombatFormulaService.Default);

        Assert.Equal(ContentCatalog.MaxEnemyMaxHealth, restored.Combatant.Health.MaxHealth);
        Assert.Equal(ContentCatalog.MaxEnemyMaxHealth, restored.Combatant.Health.CurrentHealth);
    }

    [Fact]
    public void ARestore_OfARowBelowTheCeiling_KeepsTheRowsOwnHealth()
    {
        EnemyActor restored = EnemyFactory.Restore(
            Row(currentHealth: 90, maxHealth: 250),
            Wolf(),
            2101,
            Events,
            CombatFormulaService.Default);

        Assert.Equal(250, restored.Combatant.Health.MaxHealth);
        Assert.Equal(90, restored.Combatant.Health.CurrentHealth);
    }
}
