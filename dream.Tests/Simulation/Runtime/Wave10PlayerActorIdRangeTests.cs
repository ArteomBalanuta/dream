using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Simulation.Runtime;

// Wave 10 tester, ID-1 follow-up: ReplicatedActorIdRanges partitions cube and enemy ids, but
// player actor ids are allocated separately (WorldSessionRuntime.AllocateRemoteActorId, from 2 and
// one more on every join of a new session id, never reused). A client picks a fresh
// "client-<guid>" session id on every connect, so a long-running server reaches 1000 after about
// 998 joins, and the next joiner's actor id is the first cube id (ReplicatedActorIdRanges
// .FirstWorldObjectId). Snapshots then carry a player and a cube under one replicated id.
public sealed class Wave10PlayerActorIdRangeTests
{
    private const int TerrainFloorSize = 32;

    [Fact]
    public void AThousandJoins_DoNotGiveAPlayerTheIdOfACube()
    {
        using WorldSessionRuntime runtime = new(TerrainFloorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession("primary", "Primary");

        // 999 short visits, each by a client with a fresh session id, as reconnects produce.
        for (int i = 0; i < 999; i++)
        {
            string session = $"client-{i:D4}";
            host.JoinSession(session, $"Visitor {i}");
            host.ReleaseSession(session);
        }

        JoinAcceptedMessage late = host.JoinSession("client-late", "Late Joiner");

        // The first terrain cube that is hit gets the first cube id.
        Cube terrainCube = FindExposedTerrainCube(runtime.Map, out GeometryCellKey cell);
        int cubeId = HitFromAbove(runtime, new PlayerSessionId("primary"), cell);
        Assert.True(runtime.Map.TryGetReplicatedId(terrainCube, out int mapId));
        Assert.Equal(cubeId, mapId);
        Assert.Equal(ReplicatedActorIdRanges.FirstWorldObjectId, cubeId);

        HashSet<int> liveCubeIds = [];
        foreach (Cube cube in runtime.Map.SolidCubes)
        {
            if (runtime.Map.TryGetReplicatedId(cube, out int id))
            {
                liveCubeIds.Add(id);
            }
        }

        int[] playerIdsInCubeRange = [.. runtime.Sessions.Players.Select(player => player.ActorId).Where(ReplicatedActorIdRanges.IsWorldObjectId)];
        int[] spawnedPlayerIdsThatAreCubeIds = [.. runtime.Sessions.Players
            .Where(player => player.IsSpawned)
            .Select(player => player.ActorId)
            .Where(liveCubeIds.Contains)];

        Assert.True(
            playerIdsInCubeRange.Length == 0,
            $"{playerIdsInCubeRange.Length} player actor ids are in the cube range, the late joiner has {late.ActorId}");
        Assert.Empty(spawnedPlayerIdsThatAreCubeIds);
    }

    // Formerly the known-bug companion (the 999th visitor got the first cube id 1000). Fixed: a
    // detached session releases its id and the next join reuses the lowest released id, so the
    // same 998 visits now hand the late joiner the first secondary id, and the snapshot carries
    // that id as a player only.
    [Fact]
    public void AThousandJoins_ReuseTheReleasedPlayerId_AndTheFirstCubeIdIsACube()
    {
        using WorldSessionRuntime runtime = new(TerrainFloorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        JoinAcceptedMessage primary = host.JoinSession("primary", "Primary");
        int firstSecondaryId = primary.ActorId + 1;
        for (int i = 0; i < 998; i++)
        {
            string session = $"client-{i:D4}";
            JoinAcceptedMessage visit = host.JoinSession(session, $"Visitor {i}");
            Assert.Equal(firstSecondaryId, visit.ActorId);
            host.ReleaseSession(session);
        }

        JoinAcceptedMessage late = host.JoinSession("client-late", "Late Joiner");
        Assert.Equal(firstSecondaryId, late.ActorId);
        Assert.True(ReplicatedActorIdRanges.IsPlayerId(late.ActorId));

        Cube terrainCube = FindExposedTerrainCube(runtime.Map, out GeometryCellKey cell);
        int cubeId = HitFromAbove(runtime, new PlayerSessionId("primary"), cell);
        Assert.Equal(ReplicatedActorIdRanges.FirstWorldObjectId, cubeId);
        Assert.True(runtime.Map.TryGetReplicatedId(terrainCube, out int mapId));
        ServerSnapshotEnvelope snapshot = runtime.ExportServerSnapshotEnvelope();
        Assert.DoesNotContain(snapshot.Actors, actor => actor.ActorId == mapId && actor.Kind == CreatureKind.Player);
        ActorSnapshot latePlayer = Assert.Single(snapshot.Actors, actor => actor.ActorId == late.ActorId);
        Assert.Equal(CreatureKind.Player, latePlayer.Kind);
        Assert.Equal("client-late", latePlayer.SessionId);
    }

    // Ids are reused lowest first, so the same join and leave sequence allocates the same ids on
    // every server (determinism), and a connected player's id is never handed to another.
    [Fact]
    public void JoinsAndLeaves_ReuseTheLowestReleasedId_Deterministically()
    {
        int[] first = RunJoinLeaveSequence();
        int[] second = RunJoinLeaveSequence();
        Assert.Equal(first, second);
        // a=2, b=3, c=4; b and a leave; d takes 2, e takes 3; f takes 5.
        Assert.Equal([2, 3, 4, 2, 3, 5], first);
    }

    // A session that leaves and comes back under the same session id reclaims its old id when it
    // is still free, and gets a fresh one when another player took it meanwhile; either way no two
    // spawned players share an id.
    [Fact]
    public void ReturningSession_ReclaimsItsIdOnlyWhenNoOneTookIt()
    {
        using WorldSessionRuntime runtime = new(TerrainFloorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession("primary", "Primary");
        int aId = host.JoinSession("a", "A").ActorId;

        host.ReleaseSession("a");
        Assert.Equal(aId, host.JoinSession("a", "A").ActorId);

        host.ReleaseSession("a");
        int bId = host.JoinSession("b", "B").ActorId;
        Assert.Equal(aId, bId);
        int aAgain = host.JoinSession("a", "A").ActorId;
        Assert.NotEqual(bId, aAgain);
        Assert.True(ReplicatedActorIdRanges.IsPlayerId(aAgain));

        // The local session shares Player's id with the primary seat by design; remote seats only.
        int[] spawnedIds = [.. runtime.Sessions.Players.Where(player => player.IsSpawned && !player.IsLocal).Select(player => player.ActorId)];
        Assert.Equal(spawnedIds.Length, spawnedIds.Distinct().Count());
        ServerSnapshotEnvelope snapshot = runtime.ExportServerSnapshotEnvelope();
        int[] playerActorIds = [.. snapshot.Actors.Where(actor => actor.Kind == CreatureKind.Player).Select(actor => actor.ActorId)];
        Assert.Equal(playerActorIds.Length, playerActorIds.Distinct().Count());
        Assert.Contains(aAgain, playerActorIds);
        Assert.Contains(bId, playerActorIds);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(998, 999)]
    [InlineData(999, ReplicatedActorIdRanges.FirstOverflowPlayerId)]
    [InlineData(ReplicatedActorIdRanges.FirstOverflowPlayerId, ReplicatedActorIdRanges.FirstOverflowPlayerId + 1)]
    public void NextPlayerId_SkipsTheCubeAndEnemyRanges(int current, int expected)
    {
        Assert.Equal(expected, ReplicatedActorIdRanges.NextPlayerId(current));
        Assert.True(ReplicatedActorIdRanges.IsPlayerId(expected));
        Assert.False(ReplicatedActorIdRanges.IsWorldObjectId(expected));
        Assert.False(ReplicatedActorIdRanges.IsEnemyId(expected));
    }

    [Fact]
    public void PlayerCubeAndEnemyRanges_AreDisjoint()
    {
        int[] boundaries =
        [
            ReplicatedActorIdRanges.FirstPlayerId, ReplicatedActorIdRanges.FirstWorldObjectId - 1,
            ReplicatedActorIdRanges.FirstWorldObjectId, ReplicatedActorIdRanges.FirstEnemyId - 1,
            ReplicatedActorIdRanges.FirstEnemyId, ReplicatedActorIdRanges.LastEnemyId,
            ReplicatedActorIdRanges.FirstOverflowWorldObjectId, ReplicatedActorIdRanges.LastWorldObjectId,
            ReplicatedActorIdRanges.FirstOverflowPlayerId, int.MaxValue,
        ];
        foreach (int id in boundaries)
        {
            int kinds = (ReplicatedActorIdRanges.IsPlayerId(id) ? 1 : 0) +
                (ReplicatedActorIdRanges.IsWorldObjectId(id) ? 1 : 0) +
                (ReplicatedActorIdRanges.IsEnemyId(id) ? 1 : 0);
            Assert.True(kinds == 1, $"id {id} is in {kinds} ranges");
        }

        Assert.Throws<InvalidOperationException>(() => ReplicatedActorIdRanges.NextPlayerId(int.MaxValue));
    }

    private static int[] RunJoinLeaveSequence()
    {
        using WorldSessionRuntime runtime = new(TerrainFloorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession("primary", "Primary");
        List<int> ids = [];
        ids.Add(host.JoinSession("a", "A").ActorId);
        ids.Add(host.JoinSession("b", "B").ActorId);
        ids.Add(host.JoinSession("c", "C").ActorId);
        host.ReleaseSession("b");
        host.ReleaseSession("a");
        ids.Add(host.JoinSession("d", "D").ActorId);
        ids.Add(host.JoinSession("e", "E").ActorId);
        ids.Add(host.JoinSession("f", "F").ActorId);
        return [.. ids];
    }

    private static Cube FindExposedTerrainCube(Map map, out GeometryCellKey cell)
    {
        foreach (Cube candidate in map.SolidCubes.OrderBy(cube => cube.Position.X).ThenBy(cube => cube.Position.Z).ThenBy(cube => cube.Position.Y))
        {
            if (!map.TryGetCellForCube(candidate, out GeometryCellKey key) ||
                key.Y < 1 ||
                key.Y + 2 >= map.Height ||
                map.TryGetCubeAtCell(key.X, key.Y + 1, key.Z) != null ||
                map.TryGetCubeAtCell(key.X, key.Y + 2, key.Z) != null ||
                map.TryGetReplicatedId(candidate, out _))
            {
                continue;
            }

            cell = key;
            return candidate;
        }

        throw new InvalidOperationException("The generated terrain has no exposed cube above the floor.");
    }

    private static int HitFromAbove(WorldSessionRuntime runtime, PlayerSessionId session, GeometryCellKey cell)
    {
        runtime.Enemies.Clear();
        runtime.SetAuthoritativeInteractionMode(PlayerInteractionMode.Combat);
        Vector3 origin = new(cell.X + 0.5f, cell.Y + 2.2f, cell.Z + 0.5f);
        runtime.Player.SetWorldPosition(origin);
        runtime.Player.SetFacing(-Vector3.UnitY);
        AuthoritativeActionResult result = runtime.ProcessAuthoritativeAction(
            new PlayerActionMessage(session, 1, new CombatUseIntent(origin, -Vector3.UnitY)));
        Assert.True(result.Accepted, result.Rejection?.ToString());
        CombatResolutionResult combat = Assert.IsType<CombatResolutionResult>(result.Payload);
        Assert.True(combat.DamageApplied > 0, $"The hit missed: target {combat.TargetActorId}.");
        return combat.TargetActorId ?? throw new InvalidOperationException("The hit has no target id.");
    }
}
