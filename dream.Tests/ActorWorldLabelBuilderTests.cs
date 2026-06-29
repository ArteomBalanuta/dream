using OpenTK.Mathematics;
using Xunit;

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
        Assert.Equal("87/100", label.HealthLabel);
    }

    [Fact]
    public void CreateEnemyLabels_UsesEnemyDisplayNameAndHealthLine()
    {
        EnemyActor enemy = CreateEnemy("Wolf", new Vector3(2f, 0f, 3f), actorId: 2000, maxHealth: 100);
        enemy.Combatant.ApplyDamage(13);

        IReadOnlyList<WorldActorLabelState> labels = ActorWorldLabelBuilder.CreateEnemyLabels([enemy]);

        WorldActorLabelState label = Assert.Single(labels);
        Assert.Equal("Wolf", label.DisplayName);
        Assert.Equal("87/100", label.HealthLabel);
    }

    private static EnemyActor CreateEnemy(string displayName, Vector3 position, int actorId, int maxHealth)
    {
        EnemyCombatant combatant = new(() => position, actorId: actorId, maxHealth: maxHealth, attackDamage: 10, attackIntervalSeconds: 1f);
        return new EnemyActor(combatant, position, aggroRange: 5f, attackRange: 1.25f, moveSpeed: 3f, displayName: displayName);
    }
}
