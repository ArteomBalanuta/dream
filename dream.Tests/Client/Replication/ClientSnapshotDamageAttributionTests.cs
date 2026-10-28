using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Presentation;
using Dream.Client.Replication;
using Dream.Client.World;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Utilities;

namespace Dream.Tests.Client.Replication;

// The server reuses a player actor id once its session leaves, and damage events carry only the
// id. A player damage event is shown only when the same session held the id in the previous
// snapshot and in this one; otherwise it may belong to the player who left.
public class ClientSnapshotDamageAttributionTests
{
    private const string LocalSession = "session-b";
    private const string LeavingSession = "session-a";

    [Fact]
    public void Player_Damage_Is_Applied_When_The_Same_Session_Held_The_Id_At_Both_Ends()
    {
        using ClientWorldSession runtime = ClientAuthoritativeStateApplierTests.CreateRuntime();
        int localId = runtime.Player.Combatant.Actor.Id;
        ClientScenePresenter presenter = new(LocalSession);
        List<DamageAppliedEvent> observed = Observe(out SceneEventHub events, out IDisposable subscription);
        using (subscription)
        {
            presenter.ApplySnapshot(Snapshot(10, [Player(localId, LocalSession)], []));

            ServerSnapshotEnvelope snapshot = Snapshot(11, [Player(localId, LocalSession)], [PlayerDamage(localId, 9)]);
            ClientSnapshotDamageApplier.ApplyToRuntime(runtime, events, snapshot, presenter);
            presenter.ApplySnapshot(snapshot);
        }

        DamageAppliedEvent damage = Assert.Single(observed);
        Assert.Equal(localId, damage.Actor.Id);
        Assert.Equal(9, damage.Amount);
    }

    [Fact]
    public void Damage_Of_A_Player_Who_Left_Does_Not_Land_On_The_Joiner_Who_Reused_The_Id()
    {
        using ClientWorldSession runtime = ClientAuthoritativeStateApplierTests.CreateRuntime();
        int localId = runtime.Player.Combatant.Actor.Id;
        // Seen from a client already connected: A held the id in the previous snapshot; in this
        // one A has left and B joined under the same id. A's damage arrives with B's actor.
        ClientScenePresenter presenter = new("observer");
        presenter.ApplySnapshot(Snapshot(10, [Player(localId, LeavingSession)], []));
        List<DamageAppliedEvent> observed = Observe(out SceneEventHub events, out IDisposable subscription);
        using (subscription)
        {
            ServerSnapshotEnvelope snapshot = Snapshot(
                11,
                [Player(localId, LocalSession)],
                [PlayerDamage(localId, 40), PlayerDied(localId)]);
            ClientSnapshotDamageApplier.ApplyToRuntime(runtime, events, snapshot, presenter);

            Assert.False(ClientSnapshotDamageApplier.IsAttributable(snapshot.DamageEvents[0], snapshot, presenter));
            Assert.False(ClientSnapshotDamageApplier.IsAttributable(snapshot.DamageEvents[1], snapshot, presenter));
        }

        Assert.Empty(observed);
    }

    [Fact]
    public void A_Joiners_First_Snapshot_Does_Not_Apply_Damage_Queued_Before_The_Join()
    {
        using ClientWorldSession runtime = ClientAuthoritativeStateApplierTests.CreateRuntime();
        int localId = runtime.Player.Combatant.Actor.Id;
        ClientScenePresenter presenter = new();
        // The join already lists the joiner under the reused id, but the next snapshot still
        // carries the leaver's damage under that id: the join is no baseline.
        presenter.ApplyJoinAccepted(Join(LocalSession, localId));
        Assert.False(presenter.TryGetLastSnapshotPlayerSession(localId, out _));

        List<DamageAppliedEvent> observed = Observe(out SceneEventHub events, out IDisposable subscription);
        using (subscription)
        {
            ServerSnapshotEnvelope first = Snapshot(21, [Player(localId, LocalSession)], [PlayerDamage(localId, 25)]);
            ClientSnapshotDamageApplier.ApplyToRuntime(runtime, events, first, presenter);
            presenter.ApplySnapshot(first);
            Assert.Empty(observed);

            // From the second snapshot on, the joiner's own damage shows.
            ServerSnapshotEnvelope second = Snapshot(22, [Player(localId, LocalSession)], [PlayerDamage(localId, 6)]);
            ClientSnapshotDamageApplier.ApplyToRuntime(runtime, events, second, presenter);
            presenter.ApplySnapshot(second);
        }

        DamageAppliedEvent damage = Assert.Single(observed);
        Assert.Equal(6, damage.Amount);
    }

    [Fact]
    public void Player_Damage_Is_Dropped_When_The_Id_Vanished_Or_Appeared_In_The_Interval()
    {
        ClientScenePresenter presenter = new("observer");
        presenter.ApplySnapshot(Snapshot(10, [Player(5, LeavingSession)], []));

        ServerSnapshotEnvelope vanished = Snapshot(11, [], [PlayerDamage(5, 3)]);
        Assert.False(ClientSnapshotDamageApplier.IsAttributable(vanished.DamageEvents[0], vanished, presenter));

        ServerSnapshotEnvelope appeared = Snapshot(11, [Player(6, LocalSession)], [PlayerDamage(6, 3)]);
        Assert.False(ClientSnapshotDamageApplier.IsAttributable(appeared.DamageEvents[0], appeared, presenter));

        ServerSnapshotEnvelope stable = Snapshot(11, [Player(5, LeavingSession)], [PlayerDamage(5, 3)]);
        Assert.True(ClientSnapshotDamageApplier.IsAttributable(stable.DamageEvents[0], stable, presenter));
    }

    [Fact]
    public void Enemy_Damage_Needs_No_Player_History()
    {
        ClientScenePresenter presenter = new();
        DamageEventDelta enemyDamage = new(3001, CreatureKind.Enemy, 11, ActorDied: false, CurrentHealth: 59, MaxHealth: 100);
        // An enemy reusing a player's number is still an enemy event.
        ServerSnapshotEnvelope snapshot = Snapshot(11, [Player(3001, LocalSession)], [enemyDamage]);

        Assert.True(ClientSnapshotDamageApplier.IsAttributable(enemyDamage, snapshot, presenter));
    }

    [Fact]
    public void Presenter_History_Follows_The_Last_Snapshot_Including_The_Local_Player()
    {
        ClientScenePresenter presenter = new(LocalSession);
        presenter.ApplySnapshot(Snapshot(10, [Player(1, LocalSession), Player(2, LeavingSession)], []));

        Assert.True(presenter.TryGetLastSnapshotPlayerSession(1, out string? local));
        Assert.Equal(LocalSession, local);
        Assert.True(presenter.TryGetLastSnapshotPlayerSession(2, out string? remote));
        Assert.Equal(LeavingSession, remote);

        presenter.ApplySnapshot(Snapshot(11, [Player(1, LocalSession), Player(2, "session-c")], []));

        Assert.True(presenter.TryGetLastSnapshotPlayerSession(2, out string? reused));
        Assert.Equal("session-c", reused);

        presenter.ApplySnapshot(Snapshot(12, [Player(1, LocalSession)], []));

        Assert.False(presenter.TryGetLastSnapshotPlayerSession(2, out _));
    }

    [Fact]
    public void Attribution_And_History_Allocate_Nothing_Per_Snapshot()
    {
        ClientScenePresenter presenter = new(LocalSession);
        ServerSnapshotEnvelope snapshot = Snapshot(
            10,
            [Player(1, LocalSession), Player(2, LeavingSession), Player(3, "session-c")],
            [PlayerDamage(2, 4), PlayerDamage(3, 5)]);
        presenter.ApplySnapshot(snapshot);
        presenter.ApplySnapshot(snapshot);
        DamageEventDelta first = snapshot.DamageEvents[0];
        DamageEventDelta second = snapshot.DamageEvents[1];
        bool attributable = false;

        long bytes = AllocationAssert.AtMost(0, () =>
        {
            attributable = ClientSnapshotDamageApplier.IsAttributable(first, snapshot, presenter) &&
                ClientSnapshotDamageApplier.IsAttributable(second, snapshot, presenter);
            presenter.TryGetLastSnapshotPlayerSession(2, out _);
        });

        Assert.Equal(0, bytes);
        Assert.True(attributable);
    }

    private static List<DamageAppliedEvent> Observe(out SceneEventHub events, out IDisposable subscription)
    {
        List<DamageAppliedEvent> observed = [];
        events = new SceneEventHub();
        subscription = events.Subscribe<DamageAppliedEvent>(evt => observed.Add(evt));
        return observed;
    }

    private static ActorSnapshot Player(int actorId, string sessionId)
    {
        return new ActorSnapshot(
            actorId,
            CreatureKind.Player,
            sessionId,
            new Vector3(5f, 1.5f, 5f),
            -Vector3.UnitZ,
            100,
            100,
            true);
    }

    private static DamageEventDelta PlayerDamage(int actorId, int amount)
    {
        return new DamageEventDelta(actorId, CreatureKind.Player, amount, ActorDied: false, CurrentHealth: 100 - amount, MaxHealth: 100);
    }

    private static DamageEventDelta PlayerDied(int actorId)
    {
        return new DamageEventDelta(actorId, CreatureKind.Player, Amount: 0, ActorDied: true, CurrentHealth: 0, MaxHealth: 100);
    }

    private static ServerSnapshotEnvelope Snapshot(
        ulong tick,
        IReadOnlyList<ActorSnapshot> actors,
        IReadOnlyList<DamageEventDelta> damageEvents)
    {
        return new ServerSnapshotEnvelope(
            tick,
            new Dictionary<string, ulong>(),
            actors,
            PlayerInteractionMode.Combat,
            null,
            Array.Empty<WorldMutationDelta>(),
            damageEvents);
    }

    private static JoinAcceptedMessage Join(string sessionId, int actorId)
    {
        return new JoinAcceptedMessage(
            sessionId,
            actorId,
            20,
            InitialWorld: null,
            Roster: [new PlayerRosterEntry(sessionId, actorId, sessionId)],
            InitialActors: [Player(actorId, sessionId)]);
    }
}
