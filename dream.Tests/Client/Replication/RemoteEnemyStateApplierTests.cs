using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Replication;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.World;

namespace Dream.Tests.Client.Replication;


public class RemoteEnemyStateApplierTests
{
    [Fact]
    public void ApplyActorSnapshots_ReplacesRegistryWithAuthoritativeEnemyStates()
    {
        EnemyRegistry registry = new();
        registry.Add(CreateEnemy(actorId: 2000, position: new Vector3(1f, 0f, 1f)));

        RemoteEnemyStateApplier.ApplyActorSnapshots(
            registry,
            [
                new ActorSnapshot(3000, CreatureKind.Enemy, null, new Vector3(4f, 0f, 6f), Vector3.UnitX, 55, 80, true, null, null, "Wolf Alpha"),
                new ActorSnapshot(3001, CreatureKind.Enemy, null, new Vector3(8f, 0f, 2f), -Vector3.UnitZ, 30, 45, true, null, null, "Wolf Beta")
            ]);

        Assert.Equal(2, registry.ActiveActors.Count);
        Assert.Collection(
            registry.ActiveActors.OrderBy(actor => actor.Combatant.Actor.Id),
            first =>
            {
                Assert.Equal(3000, first.Combatant.Actor.Id);
                Assert.Equal("Wolf Alpha", first.DisplayName);
                Assert.Equal(4f, first.Position.X, 3);
                Assert.Equal(6f, first.Position.Z, 3);
                Assert.Equal(55, first.Combatant.Health.CurrentHealth);
                Assert.Equal(80, first.Combatant.Health.MaxHealth);
                Assert.Equal(1f, first.Forward.X, 3);
            },
            second =>
            {
                Assert.Equal(3001, second.Combatant.Actor.Id);
                Assert.Equal("Wolf Beta", second.DisplayName);
                Assert.Equal(8f, second.Position.X, 3);
                Assert.Equal(2f, second.Position.Z, 3);
                Assert.Equal(30, second.Combatant.Health.CurrentHealth);
                Assert.Equal(45, second.Combatant.Health.MaxHealth);
            });
    }

    [Fact]
    public void ApplyActorSnapshots_ExcludesDeadEnemiesFromActiveRegistry()
    {
        EnemyRegistry registry = new();

        RemoteEnemyStateApplier.ApplyActorSnapshots(
            registry,
            [
                new ActorSnapshot(3000, CreatureKind.Enemy, null, new Vector3(4f, 0f, 6f), Vector3.UnitX, 0, 80, false, null, null, "Wolf")
            ]);

        Assert.Empty(registry.ActiveActors);
    }

    [Fact]
    public void ApplyActorSnapshots_UpdatesExistingEnemyAndPreservesVisualState()
    {
        EnemyRegistry registry = new();
        EnemyActor existing = CreateEnemy(actorId: 3000, position: new Vector3(1f, 0f, 1f));
        registry.Add(existing);

        RemoteEnemyStateApplier.ApplyActorSnapshots(
            registry,
            [
                new ActorSnapshot(
                    3000,
                    CreatureKind.Enemy,
                    null,
                    new Vector3(4f, 0f, 6f),
                    Vector3.UnitX,
                    55,
                    80,
                    true,
                    null,
                    null,
                    "Wolf Alpha",
                    null,
                    EnemyBehaviorState.Chasing)
            ]);

        EnemyActor actor = Assert.Single(registry.ActiveActors);
        Assert.Same(existing, actor);
        Assert.Same(existing.VisualState, actor.VisualState);
        Assert.Equal(new Vector3(4f, 0f, 6f), actor.Position);
        Assert.Equal(Vector3.UnitX, actor.Forward);
        Assert.Equal(EnemyBehaviorState.Chasing, actor.BehaviorState);
        Assert.Equal(55, actor.Combatant.Health.CurrentHealth);
        Assert.Equal(80, actor.Combatant.Health.MaxHealth);
    }

    private static EnemyActor CreateEnemy(int actorId, Vector3 position)
    {
        EnemyCombatant combatant = new(
            () => position,
            actorId: actorId,
            maxHealth: 80,
            attackDamage: 10,
            attackIntervalSeconds: 1f);
        return new EnemyActor(combatant, position, aggroRange: 5f, attackRange: 1.25f, moveSpeed: 3f);
    }
}
