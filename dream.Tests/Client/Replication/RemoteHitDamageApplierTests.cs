using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Presentation;
using Dream.Client.Replication;
using Dream.Client.World;
using Dream.Multiplayer.Protocol;
using Dream.Rendering.Hud;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Utilities;

namespace Dream.Tests.Client.Replication;

// A hit on another player must draw at that player's position in the snapshot that carries it.
public class RemoteHitDamageApplierTests
{
    private const int PeerActorId = 8;
    private const string PeerSession = "session-peer";

    private static ActorSnapshot Peer(Vector3 position) =>
        new(PeerActorId, CreatureKind.Player, PeerSession, position, -Vector3.UnitZ, 100, 100, true);

    private static ServerSnapshotEnvelope Snapshot(ActorSnapshot[] actors, params DamageEventDelta[] damage) =>
        new(
            1,
            new Dictionary<string, ulong>(),
            actors,
            PlayerInteractionMode.Combat,
            null,
            Array.Empty<WorldMutationDelta>(),
            damage);

    // The presenter's history needs the peer in a previous snapshot before a hit on it is attributable.
    private static ClientScenePresenter PresenterThatSawPeer()
    {
        ClientScenePresenter presenter = new("session-local");
        presenter.ApplySnapshot(Snapshot([Peer(new Vector3(1f, 0f, 1f))]));
        return presenter;
    }

    private static DamageEventDelta Hit(int actorId, CreatureKind kind, int amount = 12) =>
        new(actorId, kind, amount, ActorDied: false, CurrentHealth: 50, MaxHealth: 100);

    [Fact]
    public void RemoteHit_OnPeer_PublishesAtThePeersPosition()
    {
        using ClientWorldSession runtime = ClientAuthoritativeStateApplierTests.CreateRuntime();
        SceneEventHub events = new();
        List<DamageAppliedEvent> seen = [];
        using IDisposable sub = events.Subscribe<DamageAppliedEvent>(e => seen.Add(e));
        Vector3 at = new(20f, 3f, -4f);

        ClientSnapshotDamageApplier.ApplyToRuntime(
            runtime, events, Snapshot([Peer(at)], Hit(PeerActorId, CreatureKind.Player)), PresenterThatSawPeer());

        DamageAppliedEvent e = Assert.Single(seen);
        Assert.Equal(PeerActorId, e.Actor.Id);
        Assert.Equal(12, e.Amount);
        Vector3 anchor = e.Actor.GetWorldAnchor();
        Assert.Equal(at.X, anchor.X, 3);
        Assert.Equal(at.Z, anchor.Z, 3);
        Assert.True(anchor.Y > at.Y);
    }

    [Fact]
    public void RemoteHit_FollowsThePeerAcrossSnapshots()
    {
        using ClientWorldSession runtime = ClientAuthoritativeStateApplierTests.CreateRuntime();
        SceneEventHub events = new();
        List<Vector3> anchors = [];
        using IDisposable sub = events.Subscribe<DamageAppliedEvent>(e => anchors.Add(e.Actor.GetWorldAnchor()));
        ClientScenePresenter presenter = PresenterThatSawPeer();

        ClientSnapshotDamageApplier.ApplyToRuntime(
            runtime, events, Snapshot([Peer(new Vector3(5f, 0f, 5f))], Hit(PeerActorId, CreatureKind.Player)), presenter);
        ClientSnapshotDamageApplier.ApplyToRuntime(
            runtime, events, Snapshot([Peer(new Vector3(9f, 0f, 2f))], Hit(PeerActorId, CreatureKind.Player)), presenter);

        Assert.Equal(2, anchors.Count);
        Assert.Equal(5f, anchors[0].X, 3);
        Assert.Equal(9f, anchors[1].X, 3);
    }

    [Fact]
    public void RemoteHit_PeerAbsentFromSnapshot_IsSkippedNotDrawnAtOrigin()
    {
        using ClientWorldSession runtime = ClientAuthoritativeStateApplierTests.CreateRuntime();
        SceneEventHub events = new();
        List<DamageAppliedEvent> seen = [];
        using IDisposable sub = events.Subscribe<DamageAppliedEvent>(e => seen.Add(e));

        ClientSnapshotDamageApplier.ApplyToRuntime(
            runtime, events, Snapshot([], Hit(PeerActorId, CreatureKind.Player)), PresenterThatSawPeer());

        Assert.Empty(seen);
    }

    [Fact]
    public void RemoteHit_UnknownActors_AreSkippedQuietly()
    {
        using ClientWorldSession runtime = ClientAuthoritativeStateApplierTests.CreateRuntime();
        SceneEventHub events = new();
        List<DamageAppliedEvent> seen = [];
        using IDisposable sub = events.Subscribe<DamageAppliedEvent>(e => seen.Add(e));

        ClientSnapshotDamageApplier.ApplyToRuntime(
            runtime, events, Snapshot([Peer(Vector3.One)], Hit(999, CreatureKind.Player)), PresenterThatSawPeer());
        ClientSnapshotDamageApplier.ApplyToRuntime(
            runtime, events, Snapshot([], Hit(4242, CreatureKind.Enemy)), new ClientScenePresenter());

        Assert.Empty(seen);
    }

    [Fact]
    public void RemoteHit_LocalPlayerStillResolvesToTheLocalCombatant()
    {
        using ClientWorldSession runtime = ClientAuthoritativeStateApplierTests.CreateRuntime();
        SceneEventHub events = new();
        List<DamageAppliedEvent> seen = [];
        using IDisposable sub = events.Subscribe<DamageAppliedEvent>(e => seen.Add(e));
        int localId = runtime.Player.Combatant.Actor.Id;
        ClientScenePresenter presenter = new("session-local");
        ActorSnapshot local = new(localId, CreatureKind.Player, "session-local", Vector3.One, -Vector3.UnitZ, 100, 100, true);
        presenter.ApplySnapshot(Snapshot([local]));

        ClientSnapshotDamageApplier.ApplyToRuntime(
            runtime, events, Snapshot([local], Hit(localId, CreatureKind.Player)), presenter);

        DamageAppliedEvent e = Assert.Single(seen);
        Assert.Same(runtime.Player.Combatant.Actor.AnchorProvider, e.Actor.AnchorProvider);
        Assert.False(runtime.TryResolveRemotePlayerActor(localId, Vector3.Zero, out _));
    }

    [Fact]
    public void RemoteHit_EnemyStillResolvesToTheEnemy()
    {
        using ClientWorldSession runtime = ClientAuthoritativeStateApplierTests.CreateRuntime();
        SceneEventHub events = new();
        List<DamageAppliedEvent> seen = [];
        using IDisposable sub = events.Subscribe<DamageAppliedEvent>(e => seen.Add(e));
        RemoteEnemyStateApplier.ApplyActorSnapshots(runtime.Enemies,
        [
            new ActorSnapshot(3001, CreatureKind.Enemy, null, new Vector3(6f, 1.5f, 6f), -Vector3.UnitZ, 70, 100, true)
        ]);
        runtime.Enemies.TryGetActorById(3001, out CombatActorHandle enemy);

        ClientSnapshotDamageApplier.ApplyToRuntime(
            runtime, events, Snapshot([], Hit(3001, CreatureKind.Enemy, 11)), new ClientScenePresenter());

        DamageAppliedEvent e = Assert.Single(seen);
        Assert.Same(enemy.AnchorProvider, e.Actor.AnchorProvider);
    }

    [Fact]
    public void RemoteHit_OnPeer_AllocatesNothingOnceTheHandleExists()
    {
        using ClientWorldSession runtime = ClientAuthoritativeStateApplierTests.CreateRuntime();
        SceneEventHub events = new();
        ClientScenePresenter presenter = PresenterThatSawPeer();
        ServerSnapshotEnvelope snapshot = Snapshot([Peer(new Vector3(5f, 0f, 5f))], Hit(PeerActorId, CreatureKind.Player));

        long bytes = AllocationAssert.AtMost(
            0,
            () => ClientSnapshotDamageApplier.ApplyToRuntime(runtime, events, snapshot, presenter));

        Assert.Equal(0, bytes);
    }
}
