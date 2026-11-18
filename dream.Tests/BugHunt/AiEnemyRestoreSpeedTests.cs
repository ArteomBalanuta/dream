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

// BH-AI-4: EnemyFactory.Restore clamped every StatId into the restored sheet but handed the actor the
// raw movement_speed column as its chase speed, so a NaN row froze the enemy for good (Math.Max(0, NaN)
// is NaN, and every step > 0 test then fails) and a huge row closed the whole gap to the player in one
// tick. The actor now moves at the speed its own clamped, replicated sheet holds - the same defect class
// as the max_health row, fixed on the same restore path.
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

    [Fact]
    public void ARestore_OfANaNMovementSpeedRow_MovesAtTheClampedSpeed_NotNaN()
    {
        EnemyActor restored = Restore(float.NaN, 2400);

        Assert.False(float.IsNaN(restored.MoveSpeed));
        Assert.False(float.IsNaN(restored.PatrolSpeed));
        Assert.Equal(restored.Combatant.StatSheet.Base[StatId.MovementSpeed], restored.MoveSpeed);
        Assert.Equal(StatRules.ClampEnemyBase(StatId.MovementSpeed, float.NaN), restored.MoveSpeed);
    }

    [Fact]
    public void ARestore_OfAnInfiniteOrHugeMovementSpeedRow_ClampsTheChaseSpeed()
    {
        EnemyActor infinite = Restore(float.PositiveInfinity, 2401);
        EnemyActor huge = Restore(5_000_000f, 2402);

        float ceiling = StatRules.ClampEnemyBase(StatId.MovementSpeed, float.PositiveInfinity);
        Assert.Equal(ceiling, infinite.MoveSpeed);
        Assert.Equal(ceiling, huge.MoveSpeed);
        // The actor and its own replicated sheet agree, which is the invariant that broke.
        Assert.Equal(infinite.Combatant.StatSheet.Base[StatId.MovementSpeed], infinite.MoveSpeed);
        Assert.Equal(huge.Combatant.StatSheet.Base[StatId.MovementSpeed], huge.MoveSpeed);
    }

    [Fact]
    public void ARestore_OfAnOrdinaryRow_KeepsTheRowsOwnChaseSpeed()
    {
        EnemyActor restored = Restore(2.8f, 2403);

        Assert.Equal(2.8f, restored.MoveSpeed);
        Assert.Equal(Math.Min(ContentCatalog.Shipped.Wolf.PatrolSpeed, 2.8f), restored.PatrolSpeed);
    }
}
