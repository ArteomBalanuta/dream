using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Replication;
using Dream.Client.World;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Client.Replication;

// Regression coverage for the 2026-10-27 cube duplication audit: the client mirror must not
// simulate placed cubes, move deltas must never create cubes, and the pre-join delta backlog
// must not be replayed on top of the join-time world snapshot.
public class ReplicatedCubeDuplicationTests
{
    private const int FloorSize = 12;

    [Fact]
    public void ApplyWorldMutations_CubeMovedWithoutClientCopy_DoesNotCreateCube()
    {
        Map map = new(loadVisualAssets: false, width: 10, depth: 10);
        int cubeCountBefore = map.SolidCubes.Count;

        RemoteWorldMutationApplier.ApplyWorldMutations(
            map,
            [WorldMutationDelta.Moved(new Vector3(4f, 2f, 4f), new Vector3(5.3f, 2.5f, 4.5f))]);

        Assert.Equal(cubeCountBefore, map.SolidCubes.Count);
        Assert.Null(map.TryGetCubeAtCell(4, 2, 4));
        Assert.Null(map.TryGetCubeAtCell(5, 2, 4));
    }

    [Fact]
    public void ReplicatedMirror_PlacedCube_IsKinematicAndNotLocallySimulated()
    {
        using ClientWorldSession mirror = CreateClient();
        (int x, int z) = FindOpenColumn(mirror.Map);

        RemoteWorldMutationApplier.ApplyWorldMutations(
            mirror.Map,
            [new WorldMutationDelta(WorldMutationKind.CubePlaced, new Vector3(x, 3f, z))]);
        Cube cube = Assert.IsType<Cube>(mirror.Map.TryGetCubeAtCell(x, 3, z));
        Vector3 placedCenter = cube.Position;

        BulletSharp.RigidBody body = Assert.Single(
            mirror.Physics.MapCubeBodies,
            candidate => candidate.UserObject is ValueTuple<Cube, Vector3> cubeBody &&
                ReferenceEquals(cubeBody.Item1, cube));
        Assert.True(body.CollisionFlags.HasFlag(BulletSharp.CollisionFlags.KinematicObject));
        Assert.DoesNotContain(body, mirror.Physics.DynamicMapCubeBodies);

        AdvanceClientTicks(mirror, count: 60);

        // An unsupported dynamic cube would have fallen two cells in one second.
        Assert.Same(cube, mirror.Map.TryGetCubeAtCell(x, 3, z));
        Assert.Equal(placedCenter, cube.Position);
    }

    [Fact]
    public void ClientWorld_HasNoAuthoritativeOrReplicationExportApi()
    {
        // The client used to be a full WorldSessionRuntime in "mirror" mode, which still ran
        // server paths (cube sync deltas, wolf spawn, persistence restore, authoritative actions).
        string[] methods = typeof(ClientWorldSession).GetMethods().Select(method => method.Name).ToArray();

        Assert.DoesNotContain("ProcessAuthoritativeAction", methods);
        Assert.DoesNotContain("ExportServerSnapshotEnvelope", methods);
        Assert.DoesNotContain("AttachRemoteSession", methods);
        Assert.DoesNotContain("PersistCheckpoint", methods);
    }

    [Fact]
    public void ClientWorld_StartsWithoutLocalEnemiesAndPredictsOnlyThePlayer()
    {
        using ClientWorldSession client = CreateClient();
        Vector3 start = client.Player.GetWorldPosition();

        client.AdvancePredictionTick(
            1,
            SimulationClock.FixedDeltaSeconds,
            new PlayerCommandFrame(
                PlayerSessionId.LocalDefault,
                1,
                PlayerModeSwitchIntent.None,
                new PlayerMoveIntent(ForwardAxis: 1f, StrafeAxis: 0f),
                PlayerLookIntent.None,
                PrimaryUsePressed: false),
            PlayerSimulationTuning.AuthoritativeDefault);

        Assert.Empty(client.Enemies.ActiveActors);
        Assert.NotEqual(start, client.Player.GetWorldPosition());
    }

    [Fact]
    public void Authoritative_QueuedMutations_CarryIncreasingSequences()
    {
        SceneEventHub events = new();
        using WorldSessionRuntime server = CreateRuntime(events);
        (int x, int z) = FindOpenColumn(server.Map);
        PlaceCubeWithEvent(server, events, new Vector3(x, 1f, z));
        PlaceCubeWithEvent(server, events, new Vector3(x, 2f, z));

        ServerSnapshotEnvelope export = server.ExportServerSnapshotEnvelope();

        Assert.Equal([1UL, 2UL], export.WorldMutations.Select(mutation => mutation.Sequence).ToArray());
        Assert.Equal(2UL, server.LastWorldMutationSequence);
    }

    [Fact]
    public void JoinThenBacklogSnapshot_DoesNotDuplicateCubeThatMovedBeforeJoin()
    {
        SceneEventHub serverEvents = new();
        using WorldSessionRuntime server = CreateRuntime(serverEvents);
        using RuntimeAuthoritativeSessionHost host = new(server);
        (int x, int z) = FindOpenColumn(server.Map);

        // Placed in the air, the cube falls and lands before anyone joins, leaving a
        // CubePlaced at the spawn cell followed by CubeMoved deltas in the backlog.
        Cube serverCube = PlaceCubeWithEvent(server, serverEvents, new Vector3(x, 3f, z));
        AdvanceIdleTicks(server, startTick: 1, count: 90);
        Assert.True(server.Map.TryGetCellForCube(serverCube, out var landedCell));
        Assert.Equal(1, landedCell.Y);

        JoinAcceptedMessage accepted = host.JoinSession("session-a", "Player A");
        Assert.Equal(server.LastWorldMutationSequence, accepted.InitialWorldMutationSequence);
        ServerSnapshotEnvelope backlog = host.ExportSnapshot();
        Assert.Contains(backlog.WorldMutations, mutation => mutation.Kind == WorldMutationKind.CubePlaced);

        using ClientWorldSession client = CreateClient();
        ClientJoinAcceptedApplier.ApplyToRuntime(client, accepted);
        RemoteWorldMutationApplier.ApplySnapshotToRuntime(client, backlog);

        Assert.Null(client.Map.TryGetCubeAtCell(x, 3, z));
        Assert.Null(client.Map.TryGetCubeAtCell(x, 2, z));
        Assert.NotNull(client.Map.TryGetCubeAtCell(landedCell.X, landedCell.Y, landedCell.Z));
        Assert.Equal(CountRaisedCubes(server.Map), CountRaisedCubes(client.Map));
    }

    [Fact]
    public void JoinThenNewerSnapshot_AppliesMutationsAfterJoinBaseline()
    {
        SceneEventHub serverEvents = new();
        using WorldSessionRuntime server = CreateRuntime(serverEvents);
        using RuntimeAuthoritativeSessionHost host = new(server);
        (int x, int z) = FindOpenColumn(server.Map);
        PlaceCubeWithEvent(server, serverEvents, new Vector3(x, 1f, z));

        JoinAcceptedMessage accepted = host.JoinSession("session-a", "Player A");
        _ = host.ExportSnapshot();
        PlaceCubeWithEvent(server, serverEvents, new Vector3(x, 2f, z));
        ServerSnapshotEnvelope afterJoin = host.ExportSnapshot();

        using ClientWorldSession client = CreateClient();
        ClientJoinAcceptedApplier.ApplyToRuntime(client, accepted);
        Assert.Null(client.Map.TryGetCubeAtCell(x, 2, z));
        RemoteWorldMutationApplier.ApplySnapshotToRuntime(client, afterJoin);

        Assert.NotNull(client.Map.TryGetCubeAtCell(x, 1, z));
        Assert.NotNull(client.Map.TryGetCubeAtCell(x, 2, z));
        Assert.Equal(server.LastWorldMutationSequence, client.LastAppliedReplicatedWorldMutationSequence);
    }

    [Fact]
    public void ApplySnapshotToRuntime_SkipsRepeatedSequencedMutations()
    {
        using ClientWorldSession client = CreateClient();
        (int x, int z) = FindOpenColumn(client.Map);
        WorldMutationDelta placed = new(WorldMutationKind.CubePlaced, new Vector3(x, 1f, z), Sequence: 7);
        WorldMutationDelta destroyed = new(WorldMutationKind.CubeDestroyed, new Vector3(x, 1f, z), Sequence: 8);

        RemoteWorldMutationApplier.ApplySnapshotToRuntime(client, CreateSnapshot([placed, destroyed]));
        RemoteWorldMutationApplier.ApplySnapshotToRuntime(client, CreateSnapshot([placed]));

        Assert.Null(client.Map.TryGetCubeAtCell(x, 1, z));
        Assert.Equal(8UL, client.LastAppliedReplicatedWorldMutationSequence);
    }

    private static WorldSessionRuntime CreateRuntime(SceneEventHub? events = null)
    {
        return new WorldSessionRuntime(
            FloorSize,
            new Vector3(5f, 1.5f, 5f),
            events ?? new SceneEventHub(),
            loadVisualAssets: false);
    }

    private static ClientWorldSession CreateClient()
    {
        return new ClientWorldSession(
            FloorSize,
            new Vector3(5f, 1.5f, 5f),
            new SceneEventHub(),
            loadVisualAssets: false);
    }

    private static void AdvanceClientTicks(ClientWorldSession client, int count)
    {
        for (int index = 0; index < count; index++)
        {
            client.AdvancePredictionTick(
                (ulong)index + 1,
                SimulationClock.FixedDeltaSeconds,
                PlayerCommandFrame.CreateIdle(PlayerSessionId.LocalDefault, (ulong)index + 1),
                PlayerSimulationTuning.AuthoritativeDefault);
        }
    }

    private static Cube PlaceCubeWithEvent(WorldSessionRuntime runtime, SceneEventHub events, Vector3 cell)
    {
        Cube cube = Assert.IsType<Cube>(runtime.Map.TryAddCube(cell));
        events.Publish(new CubePlacedEvent(cube, cell));
        return cube;
    }

    private static (int X, int Z) FindOpenColumn(Map map)
    {
        for (int x = 1; x < map.Width - 1; x++)
        {
            for (int z = 1; z < map.Depth - 1; z++)
            {
                bool nearPlayerSpawn = Math.Abs(x - 5) <= 1 && Math.Abs(z - 5) <= 1;
                if (nearPlayerSpawn)
                {
                    continue;
                }

                bool open = Enumerable.Range(1, 4).All(y =>
                    map.TryGetCubeAtCell(x, y, z) == null &&
                    map.TryGetGeometryCellAtCell(x, y, z) == null);
                if (open)
                {
                    return (x, z);
                }
            }
        }

        throw new InvalidOperationException("No open column found in the startup world.");
    }

    private static int CountRaisedCubes(Map map)
    {
        return map.SolidCubes.Count(cube => map.TryGetCellForCube(cube, out var cell) && cell.Y > 0);
    }

    private static void AdvanceIdleTicks(WorldSessionRuntime runtime, ulong startTick, int count)
    {
        for (int index = 0; index < count; index++)
        {
            runtime.AdvanceAuthoritativeTick(
                startTick + (ulong)index,
                SimulationClock.FixedDeltaSeconds,
                new PlayerCommandFrame(
                    PlayerSessionId.LocalDefault,
                    Sequence: startTick + (ulong)index,
                    PlayerModeSwitchIntent.None,
                    new PlayerMoveIntent(ForwardAxis: 0f, StrafeAxis: 0f),
                    PlayerLookIntent.None,
                    PrimaryUsePressed: false),
                new PlayerSimulationTuning(
                    EyeHeightOffset: 0.35f,
                    MouseSensitivity: 2.5f,
                    MoveSpeed: 6f,
                    NoclipSpeed: 150f,
                    JumpVelocity: 5f),
                allowGameplaySimulation: false);
        }
    }

    private static ServerSnapshotEnvelope CreateSnapshot(IReadOnlyList<WorldMutationDelta> mutations)
    {
        return new ServerSnapshotEnvelope(
            1,
            new Dictionary<string, ulong>(),
            Array.Empty<ActorSnapshot>(),
            PlayerInteractionMode.Building,
            null,
            mutations,
            Array.Empty<DamageEventDelta>());
    }
}
