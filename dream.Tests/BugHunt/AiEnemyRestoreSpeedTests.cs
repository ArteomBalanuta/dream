using OpenTK.Mathematics;
using Dream.Simulation.Content;
using Dream.Simulation.Enemies;
using Dream.Simulation.Persistence;
using Dream.Simulation.Rules;
using Dream.Simulation.Runtime;
using Dream.Simulation.Stats;
using Dream.Simulation.World;
using Xunit;

namespace Dream.Tests.BugHunt;

// BH-AI-4: EnemyFactory.Restore handed the actor the raw movement_speed column as its chase speed, so a
// NaN row froze the enemy for good (Math.Max(0, NaN) is NaN, and every step > 0 test then fails) and a
// huge row closed the whole gap to the player in one tick. The chase speed is now a kind stat taken from
// the template, so no row can move an enemy at all.
public sealed class AiEnemyRestoreSpeedTests
{
    private static readonly SceneEventHub Events = new();

    private static PersistedEnemyInstance Row(float movementSpeed) => new(
        new ObjectId(2400),
        "Wolf",
        "Wolf",
        new Vector3(9f, 1f, 9f),
        Vector3.UnitZ,
        new PersistedCreatureStats(50, 100, 8, 10, 10, 0, movementSpeed, 1.0f, 1),
        DateTime.UnixEpoch,
        DateTime.UnixEpoch);

    private static EnemyActor Restore(float movementSpeed, int actorId) => EnemyFactory.Restore(
        Row(movementSpeed),
        ContentCatalog.Shipped.Wolf,
        actorId,
        Events,
        CombatFormulaService.Default);

    // The chase speed is a KIND stat now (authored, or tuned in enemy_kinds), so a hostile movement_speed
    // column is simply ignored: these tests prove the row cannot freeze, outrun or otherwise move an enemy.
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(5_000_000f)]
    [InlineData(0f)]
    [InlineData(-3f)]
    public void ARestore_OfAHostileMovementSpeedRow_IgnoresTheRow_AndMovesAtTheKindsSpeed(float hostile)
    {
        EnemyActor restored = Restore(hostile, 2400);

        EnemyTemplate wolf = ContentCatalog.Shipped.Wolf;
        Assert.False(float.IsNaN(restored.MoveSpeed));
        Assert.Equal(wolf.MoveSpeed, restored.MoveSpeed);
        Assert.Equal(wolf.PatrolSpeed, restored.PatrolSpeed);
        // The actor and its own replicated sheet agree.
        Assert.Equal(restored.Combatant.StatSheet.Base[StatId.MovementSpeed], restored.MoveSpeed);
    }

    [Fact]
    public void ARestore_OfAnOrdinaryRow_MovesAtTheKindsSpeed_NotTheRowsOwn()
    {
        EnemyActor restored = Restore(2.8f, 2403);

        Assert.Equal(ContentCatalog.Shipped.Wolf.MoveSpeed, restored.MoveSpeed);
        Assert.NotEqual(2.8f, restored.MoveSpeed);
        Assert.Equal(ContentCatalog.Shipped.Wolf.PatrolSpeed, restored.PatrolSpeed);
    }

    // The synthetic negative: a tuned kind (the enemy_kinds path) does change a restored enemy, so the
    // assertions above would fail if the restore still read the row.
    [Fact]
    public void ARestore_FromATunedCatalog_MovesAtTheTunedSpeed()
    {
        ContentCatalog tuned = ContentCatalog.Shipped.WithEnemyTuning([new EnemyKindTuning("Wolf", 2.0f, 0.5f, 2.5f, 1.35f, 10f)]);
        tuned.TryGetEnemy("Wolf", out EnemyTemplate wolf);

        EnemyActor restored = EnemyFactory.Restore(Row(5.0f), wolf, 2404, Events, CombatFormulaService.Default);

        Assert.Equal(2.0f, restored.MoveSpeed);
        Assert.Equal(0.5f, restored.PatrolSpeed);
        Assert.Equal(2.5f, restored.Combatant.AttackIntervalSeconds);
    }
}
