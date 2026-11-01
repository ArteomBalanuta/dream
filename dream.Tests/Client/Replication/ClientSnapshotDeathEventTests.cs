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

// Client combatants have no event hub, so ActorDiedEvent never fires on a client by itself. The
// snapshot's ActorDied flag is the only death signal, and the applier turns it into the event.
public class ClientSnapshotDeathEventTests
{
    private const int EnemyId = 3001;

    private sealed class Observed
    {
        public List<DamageAppliedEvent> Damage { get; } = [];
        public List<ActorDiedEvent> Deaths { get; } = [];
    }

    private static Observed Apply(params DamageEventDelta[] damage)
    {
        using ClientWorldSession runtime = ClientAuthoritativeStateApplierTests.CreateRuntime();
        SceneEventHub events = new();
        Observed observed = new();
        using IDisposable damageSubscription = events.Subscribe<DamageAppliedEvent>(evt => observed.Damage.Add(evt));
        using IDisposable deathSubscription = events.Subscribe<ActorDiedEvent>(evt => observed.Deaths.Add(evt));
        RemoteEnemyStateApplier.ApplyActorSnapshots(runtime.Enemies,
        [
            new ActorSnapshot(EnemyId, CreatureKind.Enemy, null, new Vector3(6f, 1.5f, 6f), -Vector3.UnitZ, 70, 100, true)
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

    private static DamageEventDelta Hit(int actorId, int amount, bool died, CreatureKind kind = CreatureKind.Enemy)
    {
        return new DamageEventDelta(actorId, kind, amount, died, died ? 0 : 50, 100);
    }

    [Fact]
    public void The_Two_Deltas_Of_One_Death_Publish_A_Single_Death_And_One_Damage_Event()
    {
        // The server writes the killing hit and, from the actor-died event, an Amount 0 delta.
        Observed observed = Apply(damage: [Hit(EnemyId, 0, true), Hit(EnemyId, 30, true)]);

        ActorDiedEvent death = Assert.Single(observed.Deaths);
        Assert.Equal(EnemyId, death.Actor.Id);
        DamageAppliedEvent damage = Assert.Single(observed.Damage);
        Assert.Equal(30, damage.Amount);
    }

    [Fact]
    public void The_Order_Of_The_Two_Deltas_Does_Not_Matter()
    {
        Observed observed = Apply(damage: [Hit(EnemyId, 30, true), Hit(EnemyId, 0, true)]);

        Assert.Single(observed.Deaths);
        Assert.Single(observed.Damage);
    }

    [Fact]
    public void A_Zero_Amount_Death_Delta_Publishes_No_Damage_Event()
    {
        Observed observed = Apply(damage: [Hit(EnemyId, 0, true)]);

        Assert.Single(observed.Deaths);
        Assert.Empty(observed.Damage);
    }

    [Fact]
    public void A_Zero_Amount_Delta_That_Is_Not_A_Death_Publishes_Nothing()
    {
        Observed observed = Apply(damage: [Hit(EnemyId, 0, false)]);

        Assert.Empty(observed.Deaths);
        Assert.Empty(observed.Damage);
    }

    [Fact]
    public void A_Non_Lethal_Hit_Publishes_Damage_But_No_Death()
    {
        Observed observed = Apply(damage: [Hit(EnemyId, 11, false)]);

        Assert.Empty(observed.Deaths);
        Assert.Single(observed.Damage);
    }

    [Fact]
    public void An_Actor_The_Client_Cannot_Resolve_Publishes_Nothing()
    {
        Observed observed = Apply(damage: [Hit(9999, 0, true), Hit(9999, 30, true)]);

        Assert.Empty(observed.Deaths);
        Assert.Empty(observed.Damage);
    }

    [Fact]
    public void An_Unattributable_Player_Death_Publishes_Nothing()
    {
        // No snapshot has named a session for the id, so the death may belong to a previous holder.
        Observed observed = Apply(damage: [Hit(EnemyId, 0, true, CreatureKind.Player)]);

        Assert.Empty(observed.Deaths);
        Assert.Empty(observed.Damage);
    }

    [Fact]
    public void Two_Different_Actors_Dying_In_One_Snapshot_Each_Publish_Once()
    {
        using ClientWorldSession runtime = ClientAuthoritativeStateApplierTests.CreateRuntime();
        SceneEventHub events = new();
        List<ActorDiedEvent> deaths = [];
        using IDisposable subscription = events.Subscribe<ActorDiedEvent>(evt => deaths.Add(evt));
        RemoteEnemyStateApplier.ApplyActorSnapshots(runtime.Enemies,
        [
            new ActorSnapshot(3001, CreatureKind.Enemy, null, new Vector3(6f, 1.5f, 6f), -Vector3.UnitZ, 70, 100, true),
            new ActorSnapshot(3002, CreatureKind.Enemy, null, new Vector3(8f, 1.5f, 6f), -Vector3.UnitZ, 70, 100, true)
        ]);
        ServerSnapshotEnvelope snapshot = new(
            78,
            new Dictionary<string, ulong>(),
            Array.Empty<ActorSnapshot>(),
            PlayerInteractionMode.Combat,
            null,
            Array.Empty<WorldMutationDelta>(),
            [Hit(3001, 0, true), Hit(3002, 0, true), Hit(3001, 20, true), Hit(3002, 20, true)]);

        ClientSnapshotDamageApplier.ApplyToRuntime(runtime, events, snapshot, new ClientScenePresenter());

        Assert.Equal([3001, 3002], deaths.Select(d => d.Actor.Id).OrderBy(id => id));
    }

    [Fact]
    public void A_Death_Is_Published_After_Its_Damage_So_Both_Resolve_The_Same_Actor()
    {
        using ClientWorldSession runtime = ClientAuthoritativeStateApplierTests.CreateRuntime();
        SceneEventHub events = new();
        List<string> order = [];
        using IDisposable damageSubscription = events.Subscribe<DamageAppliedEvent>(_ => order.Add("damage"));
        using IDisposable deathSubscription = events.Subscribe<ActorDiedEvent>(_ => order.Add("death"));
        RemoteEnemyStateApplier.ApplyActorSnapshots(runtime.Enemies,
        [
            new ActorSnapshot(EnemyId, CreatureKind.Enemy, null, new Vector3(6f, 1.5f, 6f), -Vector3.UnitZ, 70, 100, true)
        ]);
        ServerSnapshotEnvelope snapshot = new(
            79,
            new Dictionary<string, ulong>(),
            Array.Empty<ActorSnapshot>(),
            PlayerInteractionMode.Combat,
            null,
            Array.Empty<WorldMutationDelta>(),
            [Hit(EnemyId, 25, true)]);

        ClientSnapshotDamageApplier.ApplyToRuntime(runtime, events, snapshot, new ClientScenePresenter());

        Assert.Equal(["damage", "death"], order);
    }
}
