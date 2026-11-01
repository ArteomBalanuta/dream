using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Presentation;
using Dream.Rendering.Actors;
using Dream.Simulation.Enemies;

namespace Dream.Tests.Rendering.Actors;


public class ActorWorldLabelBuilderTests
{
    [Fact]
    public void CreateRemotePlayerLabels_UsesDisplayNameAndHealthLine()
    {
        IReadOnlyList<WorldActorLabelState> labels = ActorWorldLabelBuilder.CreateRemotePlayerLabels(
            [
                new RemotePlayerPresentationState(
                    7,
                    "remote-1",
                    "Remote One",
                    new InterpolatedActorPose(new Vector3(4f, 1.5f, 8f), -Vector3.UnitZ),
                    87,
                    100,
                    true)
            ]);

        WorldActorLabelState label = Assert.Single(labels);
        Assert.Equal("Remote One", label.DisplayName);
        Assert.Equal("Lv 1  87/100", label.HealthLabel);
    }

    [Fact]
    public void CreateRemotePlayerLabels_HidesDeadPlayers_UntilTheyRespawn()
    {
        IReadOnlyList<WorldActorLabelState> labels = ActorWorldLabelBuilder.CreateRemotePlayerLabels(
            [
                new RemotePlayerPresentationState(
                    8,
                    "remote-dead",
                    "Remote Dead",
                    new InterpolatedActorPose(new Vector3(6f, 1.5f, 8f), -Vector3.UnitZ),
                    0,
                    100,
                    false)
            ]);

        // Wave I (I-D27) reversed the old expectation on purpose: a dead remote player has no label.
        Assert.Empty(labels);
    }

    [Fact]
    public void CreateEnemyLabels_UsesEnemyDisplayNameAndHealthLine()
    {
        EnemyActor enemy = CreateEnemy("Wolf", new Vector3(2f, 0f, 3f), actorId: 2000, maxHealth: 100);
        enemy.Combatant.ApplyDamage(13);

        IReadOnlyList<WorldActorLabelState> labels = ActorWorldLabelBuilder.CreateEnemyLabels([enemy]);

        WorldActorLabelState label = Assert.Single(labels);
        Assert.Equal("Wolf", label.DisplayName);
        Assert.Equal("Lv 1  87/100", label.HealthLabel);
    }

    private static EnemyActor CreateEnemy(string displayName, Vector3 position, int actorId, int maxHealth)
    {
        EnemyCombatant combatant = new(() => position, actorId: actorId, maxHealth: maxHealth, attackDamage: 10, attackIntervalSeconds: 1f);
        return new EnemyActor(combatant, position, aggroRange: 5f, attackRange: 1.25f, moveSpeed: 3f, displayName: displayName);
    }
}
