using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Presentation;
using Dream.Client.Replication;
using Dream.Client.World;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Client.Replication;

// PlayScene applies a snapshot as add/update, then damage, then removal. These run the same three
// calls in that order so a number resolves for both a new enemy and one the snapshot kills.
public class SnapshotApplyOrderTests
{
    private static ActorSnapshot Enemy(int id, int health) =>
        new(id, CreatureKind.Enemy, null, new Vector3(6f, 1.5f, 6f), -Vector3.UnitZ, health, 100, health > 0);

    private static List<DamageAppliedEvent> Apply(ClientWorldSession runtime, ActorSnapshot[] actors, DamageEventDelta[] damage)
    {
        SceneEventHub events = new();
        List<DamageAppliedEvent> observed = [];
        using IDisposable subscription = events.Subscribe<DamageAppliedEvent>(observed.Add);
        ServerSnapshotEnvelope snapshot = new(
            77,
            new Dictionary<string, ulong>(),
            actors,
            PlayerInteractionMode.Combat,
            null,
            Array.Empty<WorldMutationDelta>(),
            damage);

        RemoteEnemyStateApplier.AddOrUpdateEnemies(runtime.Enemies, actors);
        ClientSnapshotDamageApplier.ApplyToRuntime(runtime, events, snapshot, new ClientScenePresenter());
        RemoteEnemyStateApplier.RemoveAbsentEnemies(runtime.Enemies, actors);
        return observed;
    }

    [Fact]
    public void AnEnemySpawnedAndHitInOneSnapshot_StillGetsItsNumber()
    {
        using ClientWorldSession runtime = ClientAuthoritativeStateApplierTests.CreateRuntime();

        DamageAppliedEvent evt = Assert.Single(Apply(
            runtime,
            [Enemy(3002, 80)],
            [new DamageEventDelta(3002, CreatureKind.Enemy, 20, false, 80, 100)]));

        Assert.Equal(3002, evt.Actor.Id);
        Assert.Equal(20, evt.Amount);
        Assert.Single(runtime.Enemies.ActiveActors);
    }

    [Fact]
    public void AnEnemyKilledInTheSnapshot_StillGetsItsNumber_AndIsThenRemoved()
    {
        using ClientWorldSession runtime = ClientAuthoritativeStateApplierTests.CreateRuntime();
        Apply(runtime, [Enemy(3001, 70)], []);

        DamageAppliedEvent evt = Assert.Single(Apply(
            runtime,
            [Enemy(3001, 0)],
            [new DamageEventDelta(3001, CreatureKind.Enemy, 70, true, 0, 100)]));

        Assert.Equal(3001, evt.Actor.Id);
        Assert.Empty(runtime.Enemies.ActiveActors);
    }

    [Fact]
    public void ANewEnemyAndAKilledEnemyInOneSnapshot_BothGetTheirNumbers()
    {
        using ClientWorldSession runtime = ClientAuthoritativeStateApplierTests.CreateRuntime();
        Apply(runtime, [Enemy(3001, 70)], []);

        List<DamageAppliedEvent> events = Apply(
            runtime,
            [Enemy(3001, 0), Enemy(3002, 60)],
            [
                new DamageEventDelta(3001, CreatureKind.Enemy, 70, true, 0, 100),
                new DamageEventDelta(3002, CreatureKind.Enemy, 40, false, 60, 100)
            ]);

        Assert.Equal([3001, 3002], events.Select(e => e.Actor.Id).OrderBy(id => id).ToArray());
        Assert.Equal(3002, Assert.Single(runtime.Enemies.ActiveActors).Combatant.Actor.Id);
    }
}
