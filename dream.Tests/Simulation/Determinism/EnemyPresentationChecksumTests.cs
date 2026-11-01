using OpenTK.Mathematics;
using Xunit;
using Dream.Simulation.Combat;
using Dream.Simulation.Determinism;
using Dream.Simulation.Enemies;
using Dream.Simulation.World;

namespace Dream.Tests.Simulation.Determinism;

// Wave I C0a: enemy level and visual key enter HashActor only when non-default, so a wolf-only world
// hashes exactly as it did before the fields existed.
public sealed class EnemyPresentationChecksumTests
{
    private static ActorSnapshot Wolf() =>
        new(2001, CreatureKind.Enemy, null, new Vector3(5f, 1f, 5f), Vector3.UnitX, 100, 100, true,
            DisplayName: "Wolf", EnemyBehaviorState: EnemyBehaviorState.Idle);

    [Fact]
    public void ADefaultEnemy_HashesAsBefore_AndAnExplicitDefaultChangesNothing()
    {
        ActorSnapshot wolf = Wolf();

        Assert.Equal(SimulationStateChecksum.HashActor(wolf), SimulationStateChecksum.HashActor(wolf with { EnemyLevel = 0, VisualKey = null }));
    }

    [Fact]
    public void ALevelOrAVisualKey_ChangesTheHash_AndEachValueCounts()
    {
        ActorSnapshot wolf = Wolf();
        ulong plain = SimulationStateChecksum.HashActor(wolf);
        ulong level3 = SimulationStateChecksum.HashActor(wolf with { EnemyLevel = 3 });
        ulong dire = SimulationStateChecksum.HashActor(wolf with { EnemyLevel = 3, VisualKey = "enemy.dire-wolf" });
        ulong otherKey = SimulationStateChecksum.HashActor(wolf with { EnemyLevel = 3, VisualKey = "enemy.other" });
        ulong keyOnly = SimulationStateChecksum.HashActor(wolf with { VisualKey = "enemy.dire-wolf" });

        Assert.Equal(5, new HashSet<ulong> { plain, level3, dire, otherKey, keyOnly }.Count);
    }
}
