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
        Assert.Equal(1, presenter.RemoteEnemyBufferCount);
        Assert.Equal(1, presenter.CaptureDebugState(keyframe.Snapshot.ServerTick).RemoteEntryCount);
        Assert.Contains(presenter.BuildPlayerRoster(), entry => entry.SessionId == RemoteSessionId);

        // Before the fix this pruned both: the avatar and the wolf vanished for every frame until
        // the next keyframe.
        presenter.ApplySnapshot(withheld.Snapshot);
        Assert.Equal(1, presenter.RemoteEnemyBufferCount);
        Assert.Equal(1, presenter.CaptureDebugState(withheld.Snapshot.ServerTick).RemoteEntryCount);
        Assert.Contains(presenter.BuildPlayerRoster(), entry => entry.SessionId == RemoteSessionId);
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

    // The enemy registry's own removal reads the same list, which is why PlayScene skips it on a
    // withholding snapshot: left to run, it drops every withheld enemy.
    [Fact]
    public void TheEnemyRegistryRemovalIsWhatTheWithheldListWouldHaveDropped()
    {
        ActorStateCache cache = new();
        (ServerSnapshotMessage keyframe, ServerSnapshotMessage withheld, _) = ApplyBrokenChain(cache);
        EnemyRegistry registry = new();

        RemoteEnemyStateApplier.AddOrUpdateEnemies(registry, keyframe.Snapshot.Actors);
        Assert.Single(registry.ActiveActors);

        // The guard PlayScene consults.
        Assert.True(ClientSnapshotActorCompleteness.WithholdsActors(withheld.Snapshot));

        // What running the removal anyway would do.
        RemoteEnemyStateApplier.RemoveAbsentEnemies(registry, withheld.Snapshot.Actors);
        Assert.Empty(registry.ActiveActors);
    }
}
