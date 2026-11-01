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

// The snapshot's hit kind reaches the DamageAppliedEvent the floating text reads.
public class ClientSnapshotHitKindTests
{
    private static List<DamageAppliedEvent> Apply(params DamageEventDelta[] damage)
    {
        using ClientWorldSession runtime = ClientAuthoritativeStateApplierTests.CreateRuntime();
        SceneEventHub events = new();
        List<DamageAppliedEvent> observed = [];
        using IDisposable subscription = events.Subscribe<DamageAppliedEvent>(evt => observed.Add(evt));
        RemoteEnemyStateApplier.ApplyActorSnapshots(runtime.Enemies,
        [
            new ActorSnapshot(3001, CreatureKind.Enemy, null, new Vector3(6f, 1.5f, 6f), -Vector3.UnitZ, 70, 100, true)
        ]);
        ServerSnapshotEnvelope snapshot = new(
            77,
            new Dictionary<string, ulong>(),
            Array.Empty<ActorSnapshot>(),
            PlayerInteractionMode.Combat,
            null,
            Array.Empty<WorldMutationDelta>(),
            damage);

        ClientSnapshotDamageApplier.ApplyToRuntime(runtime, events, snapshot, new ClientScenePresenter());

        return observed;
    }

    [Fact]
    public void ACriticalHit_PublishesACriticalEvent()
    {
        DamageAppliedEvent evt = Assert.Single(Apply(
            new DamageEventDelta(3001, CreatureKind.Enemy, 11, false, 59, 100, FloatingCombatTextKind.Critical)));

        Assert.Equal(FloatingCombatTextKind.Critical, evt.Kind);
        Assert.Equal(11, evt.Amount);
    }

    [Fact]
    public void ARegularOrUndefinedHit_PublishesARegularEvent()
    {
        List<DamageAppliedEvent> events = Apply(
            new DamageEventDelta(3001, CreatureKind.Enemy, 11, false, 59, 100),
            new DamageEventDelta(3001, CreatureKind.Enemy, 12, false, 47, 100, (FloatingCombatTextKind)99));

        Assert.Equal(2, events.Count);
        Assert.All(events, evt => Assert.Equal(FloatingCombatTextKind.Regular, evt.Kind));
    }
}
