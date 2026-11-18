using OpenTK.Mathematics;
using Xunit;
using Dream.Simulation.Combat;
using Dream.Simulation.Content;
using Dream.Simulation.Enemies;
using Dream.Simulation.Persistence;
using Dream.Simulation.Rules;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.BugHunt;

// BH-DEATH-2: enemy_instances only CHECKs (current_hp >= 0), so a row of 0 is storable, and
// EnemyFactory.Restore let it through (SetAuthoritativeState clamps into [0, max]). Such a row restored a
// permanently dead enemy: PruneInactive drops an inactive actor with no credit and no row delete, and the
// checkpoint loops skip !IsActive, so the row survived and burned one more enemy actor id every restart.
// Current health is now clamped to at least 1, like the maximum.
public sealed class DeathEnemyRestoreZeroHealthTests
{
    private static readonly SceneEventHub Events = new();

    private static EnemyTemplate Wolf() => new(
        "Wolf", "Wolf", Level: 1, Strength: 8, Vitality: 10, AttackSpeed: 1.0f, null,
        AggroRange: 12f, AttackRange: 1.35f, MoveSpeed: 3.2f, ContentCatalog.DefaultEnemyVisualKey, "wolf", EnemySpawnRole.NightExtra);

    private static PersistedEnemyInstance Row(int currentHealth, int maxHealth) => new(
        new ObjectId(2400),
        "Wolf",
        "Wolf",
        new Vector3(9f, 1f, 9f),
        Vector3.UnitZ,
        new PersistedCreatureStats(currentHealth, maxHealth, 8, 10, 10, 0, 2.8f, 1.0f, 1),
        DateTime.UnixEpoch,
        DateTime.UnixEpoch);

    private static EnemyActor Restore(int currentHealth, int maxHealth, int actorId) =>
        EnemyFactory.Restore(Row(currentHealth, maxHealth), Wolf(), actorId, Events, CombatFormulaService.Default);

    [Fact]
    public void ARestore_OfARowWithZeroCurrentHealth_RestoresAnActorThatIsAlive()
    {
        EnemyActor restored = Restore(currentHealth: 0, maxHealth: 40, actorId: 2400);

        Assert.True(restored.IsActive);
        Assert.Equal(1, restored.Combatant.Health.CurrentHealth);
        Assert.Equal(40, restored.Combatant.Health.MaxHealth);
    }

    [Fact]
    public void ARestore_OfARowWithNegativeCurrentHealth_RestoresAnActorThatIsAlive()
    {
        EnemyActor restored = Restore(currentHealth: -5, maxHealth: 40, actorId: 2401);

        Assert.True(restored.IsActive);
        Assert.Equal(1, restored.Combatant.Health.CurrentHealth);
    }

    // The registry prunes the inactive, so the pre-fix actor would have left the world uncredited on the
    // first tick after the restore while its row stayed behind. The restored actor must survive the prune.
    [Fact]
    public void ARestoredZeroHealthRow_IsNotPrunedAsInactive()
    {
        EnemyRegistry registry = new();
        registry.Add(Restore(currentHealth: 0, maxHealth: 40, actorId: 2402));

        registry.PruneInactive();

        Assert.Single(registry.ActiveActors);
    }

    // Control: an ordinary wounded row still restores with exactly its own health, untouched.
    [Fact]
    public void ARestore_OfAWoundedRow_KeepsItsOwnHealth()
    {
        EnemyActor restored = Restore(currentHealth: 17, maxHealth: 40, actorId: 2403);

        Assert.True(restored.IsActive);
        Assert.Equal(17, restored.Combatant.Health.CurrentHealth);
        Assert.Equal(40, restored.Combatant.Health.MaxHealth);
    }

    // Control: the clamp cannot lift current health above the clamped maximum.
    [Fact]
    public void ARestore_OfARowWithMaxHealthOne_RestoresAtOne()
    {
        EnemyActor restored = Restore(currentHealth: 0, maxHealth: 1, actorId: 2404);

        Assert.True(restored.IsActive);
        Assert.Equal(1, restored.Combatant.Health.CurrentHealth);
        Assert.Equal(1, restored.Combatant.Health.MaxHealth);
    }
}
