using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Presentation;
using Dream.Client.Replication;
using Dream.Multiplayer.Protocol;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Tests.BugHunt;

// BH-NET2-3. ActorStateCache withholds every actor it cannot reconstitute after a broken delta
// chain, rather than show stale values as current. Downstream, an actor missing from
// Snapshot.Actors meant "it left", so one broken chain despawned every enemy, NPC and remote player
// on the client until the next keyframe. A snapshot that withholds actors is now recognised as
// incomplete, and nothing is pruned on it.
public class Cli2WithheldActorPruneTests
{
    private const int EnemyActorId = 4101;
    private const int RemotePlayerActorId = 4102;
    private const string RemoteSessionId = "session-b";

    private static ActorSnapshot Enemy() => new(
        EnemyActorId,
        CreatureKind.Enemy,
        null,
        new Vector3(6f, 1.5f, 6f),
        -Vector3.UnitZ,
        80,
        100,
        true,
        DisplayName: "Wolf",
        EnemyBehaviorState: EnemyBehaviorState.Idle);

    private static ActorSnapshot RemotePlayer() => new(
        RemotePlayerActorId,
        CreatureKind.Player,
        RemoteSessionId,
        new Vector3(2f, 1.5f, 2f),
        -Vector3.UnitZ,
        100,
        100,
        true,
        DisplayName: "Player B");

    private static ServerSnapshotMessage Snapshot(ulong tick, ActorWireFrame frame) =>
        new(new ServerSnapshotEnvelope(
            tick,
            new Dictionary<string, ulong>(),
            null!,
            PlayerInteractionMode.Combat,
            null,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>())
        {
            ActorFrame = frame
        });

    // A keyframe holding both actors, then a delta frame whose base is not the frame the cache
    // holds: the chain is broken, so both actors are withheld as stale.
    private static (ServerSnapshotMessage Keyframe, ServerSnapshotMessage Broken, int Stale) ApplyBrokenChain(
        ActorStateCache cache)
    {
        ActorWireFrame keyframe = new(
            sequence: 1,
            baseSequence: 0,
            [ActorWireRecord.Full(Enemy()), ActorWireRecord.Full(RemotePlayer())]);
        ServerSnapshotMessage applied = cache.Apply(Snapshot(100, keyframe));

        ActorWireFrame broken = new(
            sequence: 9,
            baseSequence: 7,
            [
                new ActorWireRecord(EnemyActorId, ActorFieldGroups.None),
                new ActorWireRecord(RemotePlayerActorId, ActorFieldGroups.None)
            ]);
        ServerSnapshotMessage withheld = cache.Apply(Snapshot(101, broken));
        return (applied, withheld, cache.StaleActorCount);
    }

    [Fact]
    public void ABrokenChainWithholdsItsActors_AndTheSnapshotSaysSo()
    {
        ActorStateCache cache = new();
        (ServerSnapshotMessage keyframe, ServerSnapshotMessage withheld, int stale) = ApplyBrokenChain(cache);

        Assert.Equal(2, keyframe.Snapshot.Actors.Count);
        Assert.False(ClientSnapshotActorCompleteness.WithholdsActors(keyframe.Snapshot));

        // Both actors held back: the list is empty although the frame named them.
        Assert.Equal(2, stale);
        Assert.Empty(withheld.Snapshot.Actors);
        Assert.True(ClientSnapshotActorCompleteness.WithholdsActors(withheld.Snapshot));
        // The shortfall the snapshot carries is exactly the cache's own stale count, which is why
        // it can be read per snapshot instead of from the live cache.
        Assert.Equal(
            stale,
            withheld.Snapshot.ActorFrame!.Records.Length - withheld.Snapshot.Actors.Count);
    }

    [Fact]
    public void ThePresenterKeepsItsEnemiesAndRemotePlayersThroughAWithholdingSnapshot()
    {
        ActorStateCache cache = new();
        (ServerSnapshotMessage keyframe, ServerSnapshotMessage withheld, _) = ApplyBrokenChain(cache);
        ClientScenePresenter presenter = new("session-a");

        presenter.ApplySnapshot(keyframe.Snapshot);
        Assert.Equal(1, presenter.CaptureDebugState(keyframe.Snapshot.ServerTick).RemoteEntryCount);
        Assert.Contains(presenter.BuildPlayerRoster(), entry => entry.SessionId == RemoteSessionId);

        // Before the fix this pruned both: the avatar and the roster entry vanished for every frame
        // until the next keyframe.
        presenter.ApplySnapshot(withheld.Snapshot);
        Assert.Equal(1, presenter.CaptureDebugState(withheld.Snapshot.ServerTick).RemoteEntryCount);
        Assert.Contains(presenter.BuildPlayerRoster(), entry => entry.SessionId == RemoteSessionId);
    }

    // The enemy interpolation buffer is pose continuity, not existence, so it is NOT held through a
    // withholding snapshot: the withheld actor has no current pose, and holding its buffer would
    // keep drawing the last position as if it were current - the stale draw the cache withholds the
    // actor to avoid. The renderer falls back to the authoritative pose, and the enemy itself stays
    // because the registry keeps it (TheEnemyRegistryKeepsAWithheldEnemy).
    //
    // Holding the buffer here is what the first version of this fix did, and it made a wolf whose
    // join-era pose was stale render at that old position for the whole withholding window and then
    // jump forward (EnemyInterpolationReplicationIntegrationTests
    // .ChasingWolf_UnderCoalescedSnapshots_NeverRendersBackwards_AndARespawnSnaps).
    [Fact]
    public void AWithheldEnemysPoseIsNotKeptAsCurrent()
    {
        ActorStateCache cache = new();
        (ServerSnapshotMessage keyframe, ServerSnapshotMessage withheld, _) = ApplyBrokenChain(cache);
        ClientScenePresenter presenter = new("session-a");

        presenter.ApplySnapshot(keyframe.Snapshot);
        Assert.Equal(1, presenter.RemoteEnemyBufferCount);

        presenter.ApplySnapshot(withheld.Snapshot);
        Assert.Equal(0, presenter.RemoteEnemyBufferCount);
        Assert.False(presenter.TryGetEnemyRenderPose(EnemyActorId, withheld.Snapshot.ServerTick, out _));
    }

    // A complete snapshot that really drops an actor must still prune it: the fix must not turn
    // every departure into a leak.
    [Fact]
    public void ACompleteSnapshotWithoutAnActorStillPrunesIt()
    {
        ActorStateCache cache = new();
        (ServerSnapshotMessage keyframe, _, _) = ApplyBrokenChain(cache);
        ClientScenePresenter presenter = new("session-a");
        presenter.ApplySnapshot(keyframe.Snapshot);
        Assert.Equal(1, presenter.RemoteEnemyBufferCount);

        ActorWireFrame nextKeyframe = new(sequence: 2, baseSequence: 0, [ActorWireRecord.Full(RemotePlayer())]);
        ServerSnapshotMessage enemyGone = cache.Apply(Snapshot(102, nextKeyframe));
        Assert.False(ClientSnapshotActorCompleteness.WithholdsActors(enemyGone.Snapshot));

        presenter.ApplySnapshot(enemyGone.Snapshot);
        Assert.Equal(0, presenter.RemoteEnemyBufferCount);
        Assert.Equal(1, presenter.CaptureDebugState(enemyGone.Snapshot.ServerTick).RemoteEntryCount);
    }

    // The enemy registry keeps a withheld enemy, where reading the actor list alone drops it.
    [Fact]
    public void TheEnemyRegistryKeepsAWithheldEnemy()
    {
        ActorStateCache cache = new();
        (ServerSnapshotMessage keyframe, ServerSnapshotMessage withheld, _) = ApplyBrokenChain(cache);
        EnemyRegistry registry = new();

        RemoteEnemyStateApplier.AddOrUpdateEnemies(registry, keyframe.Snapshot.Actors);
        Assert.Single(registry.ActiveActors);

        // Reading the list alone, as it used to: the withheld wolf counts as gone.
        EnemyRegistry listOnly = new();
        RemoteEnemyStateApplier.AddOrUpdateEnemies(listOnly, keyframe.Snapshot.Actors);
        RemoteEnemyStateApplier.RemoveAbsentEnemies(listOnly, withheld.Snapshot.Actors);
        Assert.Empty(listOnly.ActiveActors);

        // Reading the whole snapshot: it is withheld, so it stays.
        RemoteEnemyStateApplier.RemoveAbsentEnemies(registry, withheld.Snapshot);
        Assert.Single(registry.ActiveActors);
    }

    // The regression the first version of this fix caused. A coalesced snapshot can withhold one
    // actor and carry a real death and respawn at the same time: the all-or-nothing gate skipped
    // every removal on such a snapshot, so the dead enemy's interpolation buffer survived and its
    // respawn under a fresh actor id rendered as a long slide backwards instead of a snap
    // (EnemyInterpolationReplicationIntegrationTests
    // .ChasingWolf_UnderCoalescedSnapshots_NeverRendersBackwards_AndARespawnSnaps).
    [Fact]
    public void ARespawnOnAWithholdingSnapshotStillPrunesTheDeadEnemy()
    {
        const int RespawnedEnemyActorId = 4103;
        ActorStateCache cache = new();

        // The enemy, the remote player and a second player actor whose delta record will be the
        // one withheld, so the snapshot both withholds and carries a departure.
        ActorSnapshot other = new(
            4104,
            CreatureKind.Player,
            "session-c",
            new Vector3(9f, 1.5f, 9f),
            -Vector3.UnitZ,
            100,
            100,
            true,
            DisplayName: "Player C");
        ActorWireFrame keyframe = new(
            sequence: 1,
            baseSequence: 0,
            [ActorWireRecord.Full(Enemy()), ActorWireRecord.Full(RemotePlayer()), ActorWireRecord.Full(other)]);
        ServerSnapshotMessage applied = cache.Apply(Snapshot(100, keyframe));
        Assert.Equal(3, applied.Snapshot.Actors.Count);

        ClientScenePresenter presenter = new("session-a");
        presenter.ApplySnapshot(applied.Snapshot);
        Assert.Equal(1, presenter.RemoteEnemyBufferCount);
        Assert.True(presenter.GetRemoteEnemySampleCount(EnemyActorId) > 0);
        Assert.Equal(2, presenter.CaptureDebugState(applied.Snapshot.ServerTick).RemoteEntryCount);

        // The wolf died and respawned far away under a fresh id, player C is still here but
        // withheld (its record is a delta on a chain this frame breaks), and the old wolf id is
        // simply absent: it is gone.
        ActorSnapshot respawned = new(
            RespawnedEnemyActorId,
            CreatureKind.Enemy,
            null,
            new Vector3(52.5f, 1.5f, 52.5f),
            -Vector3.UnitZ,
            100,
            100,
            true,
            DisplayName: "Wolf",
            EnemyBehaviorState: EnemyBehaviorState.Idle);
        ActorWireFrame broken = new(
            sequence: 9,
            baseSequence: 7,
            [
                ActorWireRecord.Full(respawned),
                ActorWireRecord.Full(RemotePlayer()),
                new ActorWireRecord(other.ActorId, ActorFieldGroups.None)
            ]);
        ServerSnapshotMessage withheld = cache.Apply(Snapshot(101, broken));

        // Player C is the withheld one; the dead wolf is absent because it left.
        Assert.True(ClientSnapshotActorCompleteness.WithholdsActors(withheld.Snapshot));
        HashSet<int> withheldIds = [];
        ClientSnapshotActorCompleteness.CollectWithheldActorIds(withheld.Snapshot, withheldIds);
        Assert.Equal([other.ActorId], withheldIds);
        Assert.DoesNotContain(EnemyActorId, withheldIds);

        presenter.ApplySnapshot(withheld.Snapshot);

        // The dead wolf's buffer is gone, so its respawn snaps instead of sliding from the old
        // position, and only the respawned wolf has a buffer.
        Assert.Equal(0, presenter.GetRemoteEnemySampleCount(EnemyActorId));
        Assert.Equal(1, presenter.RemoteEnemyBufferCount);
        Assert.True(presenter.GetRemoteEnemySampleCount(RespawnedEnemyActorId) > 0);
        // Player C was only withheld, so it is still here.
        Assert.Equal(2, presenter.CaptureDebugState(withheld.Snapshot.ServerTick).RemoteEntryCount);
        Assert.Contains(presenter.BuildPlayerRoster(), entry => entry.SessionId == "session-c");

        // The enemy registry agrees: the dead wolf goes, the respawn stays.
        EnemyRegistry registry = new();
        RemoteEnemyStateApplier.AddOrUpdateEnemies(registry, applied.Snapshot.Actors);
        RemoteEnemyStateApplier.AddOrUpdateEnemies(registry, withheld.Snapshot.Actors);
        RemoteEnemyStateApplier.RemoveAbsentEnemies(registry, withheld.Snapshot);
        Assert.Equal(
            [RespawnedEnemyActorId],
            registry.ActiveActors.Select(enemy => enemy.Combatant.Actor.Id).ToArray());
    }
}
