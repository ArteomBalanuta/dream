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

    [Fact(Skip = "bug: player actor ids grow into the cube id range (1000+) after ~998 joins; AllocateRemoteActorId is not partitioned (ID-1 follow-up)")]
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

    // The same scenario without the skip: documents today's numbers so the bug stays visible in the
    // normal suite. It asserts the collision that the skipped test above rejects.
    [Fact]
    public void AThousandJoins_TodayGiveTheLateJoinerTheFirstCubeId_KnownBug()
    {
        using WorldSessionRuntime runtime = new(TerrainFloorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession("primary", "Primary");
        for (int i = 0; i < 998; i++)
        {
            string session = $"client-{i:D4}";
            host.JoinSession(session, $"Visitor {i}");
            host.ReleaseSession(session);
        }

        JoinAcceptedMessage late = host.JoinSession("client-late", "Late Joiner");
        Cube terrainCube = FindExposedTerrainCube(runtime.Map, out GeometryCellKey cell);
        int cubeId = HitFromAbove(runtime, new PlayerSessionId("primary"), cell);

        Assert.Equal(ReplicatedActorIdRanges.FirstWorldObjectId, cubeId);
        Assert.Equal(cubeId, late.ActorId);
        Assert.True(runtime.Map.TryGetReplicatedId(terrainCube, out int mapId));
        ServerSnapshotEnvelope snapshot = runtime.ExportServerSnapshotEnvelope();
        ActorSnapshot player = Assert.Single(snapshot.Actors, actor => actor.ActorId == mapId);
        Assert.Equal(CreatureKind.Player, player.Kind);
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
