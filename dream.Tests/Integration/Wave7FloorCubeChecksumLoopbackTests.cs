using System.Diagnostics;
using System.Text;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Networking;
using Dream.Client.Replication;
using Dream.Client.Startup;
using Dream.Client.World;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Integration;

// Wave 7 tester: NETR-6 moved builds onto the command path and noted that a cube built with a
// horizontal facing settles on the floor (CubeMoved to y=1.4955) and the client checksum then
// mismatches. This drives that case over loopback and diffs the client and server cell sets.
public class Wave7FloorCubeChecksumLoopbackTests
{
    private readonly ITestOutputHelper _output;

    public Wave7FloorCubeChecksumLoopbackTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // Fails every run: the floor-supported cube is Dirt on the server and Grass on the client.
    [Fact(Skip = "bug: ClientAuthoritativeActionApplier re-places an accepted build as a Grass cube after the snapshot placed it with its real material (wave 7 tester report)")]
    public async Task Horizontal_Command_Path_Builds_Keep_The_Client_Checksum_In_Sync()
    {
        const string SessionId = "w7-floor-cube";
        DedicatedServerHost? host = null;
        await using IntegratedLocalServerHandle server = await IntegratedLocalServerHandle.StartAsync(
            0,
            () => host = DedicatedServerHost.CreateRuntimeAttached(persistence: null));
        Assert.True(server.IsReady, server.ErrorMessage);
        int floorSize = host!.Runtime!.Map.Width;

        using ClientWorldSession clientWorld = new(floorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        IClientWorldState world = clientWorld;

        await using RemoteClientSession client = await RemoteClientSession.ConnectAsync(
            new RemoteClientOptions("127.0.0.1", server.BoundPort, SessionId, "Floor Cube", "w7-floor-test"));

        Vector3[] facings =
        [
            new Vector3(1f, 0f, 0f), new Vector3(-1f, 0f, 0f),
            new Vector3(0f, 0f, 1f), new Vector3(0f, 0f, -1f)
        ];
        const int FramesPerFacing = 10;
        int totalBuildFrames = facings.Length * FramesPerFacing;

        Stopwatch clock = Stopwatch.StartNew();
        ulong commandSequence = 0;
        int buildFrames = 0;
        int snapshots = 0;
        int acceptedBuilds = 0;
        int checksumsAfterBuilds = 0;
        int mismatches = 0;
        int snapshotsAfterBuilds = 0;
        string? firstMismatch = null;
        bool joined = false;
        SceneEventHub events = new();
        List<string> moves = [];
        while (clock.Elapsed < TimeSpan.FromSeconds(30))
        {
            commandSequence++;
            PlayerSessionId session = new(SessionId);
            bool build = joined && snapshots > 5 && buildFrames < totalBuildFrames;
            PlayerCommandFrame frame = build
                ? new PlayerCommandFrame(
                    session,
                    commandSequence,
                    new PlayerModeSwitchIntent(PlayerInteractionMode.Building),
                    PlayerMoveIntent.None,
                    new PlayerLookIntent(0f, 0f, facings[buildFrames++ / FramesPerFacing]),
                    PrimaryUsePressed: true)
                : PlayerCommandFrame.CreateIdle(session, commandSequence);
            await client.SendCommandFrameAsync(frame);

            while (client.TryDequeueInbound(out IServerToClientMessage? message) && message != null)
            {
                switch (message)
                {
                    case JoinAcceptedMessage accepted:
                        joined = true;
                        ClientJoinAcceptedApplier.ApplyToRuntime(world, accepted);
                        break;
                    case ServerActionMessage action:
                        ClientAuthoritativeActionApplier.ApplyActionResult(world, events, action.Result, SessionId);
                        if (action.Result.Accepted && action.Result.Payload is WorldMutationResult { Applied: true })
                        {
                            acceptedBuilds++;
                        }

                        break;
                    case ServerSnapshotMessage snapshotMessage:
                        snapshots++;
                        ServerSnapshotEnvelope snapshot = snapshotMessage.Snapshot;
                        foreach (WorldMutationDelta mutation in snapshot.WorldMutations)
                        {
                            if (mutation.Kind != WorldMutationKind.CubeMoved ||
                                mutation.CellPosition != mutation.TargetCellPosition)
                            {
                                moves.Add($"seq={mutation.Sequence} {mutation.Kind} id={mutation.ActorId} {mutation.CellPosition}->{mutation.TargetCellPosition} c={mutation.CenterPosition}");
                            }
                        }

                        RemoteWorldMutationApplier.ApplySnapshotToRuntime(world, snapshot);
                        if (buildFrames >= totalBuildFrames)
                        {
                            snapshotsAfterBuilds++;
                            if (snapshot.WorldChecksum.HasValue && snapshotsAfterBuilds > 10)
                            {
                                checksumsAfterBuilds++;
                                if (RemoteWorldMutationApplier.IsWorldOutOfSync(world, snapshot))
                                {
                                    mismatches++;
                                    firstMismatch ??= DescribeDiff(host.Runtime!.Map, clientWorld.Map);
                                }
                            }
                        }

                        break;
                }
            }

            if (checksumsAfterBuilds >= 3)
            {
                break;
            }

            await Task.Delay(15);
        }

        foreach (string move in moves.Take(80))
        {
            _output.WriteLine(move);
        }

        Assert.True(acceptedBuilds > 0, "No horizontal build was accepted.");
        Assert.True(checksumsAfterBuilds >= 3, $"Only {checksumsAfterBuilds} checksums after the builds settled.");
        Assert.True(mismatches == 0, $"{mismatches} checksum mismatch(es) after {acceptedBuilds} builds: {firstMismatch}");
    }

    // Root cause, without the network. The server sends each tick's snapshot before that tick's action
    // results (DedicatedServerService.AdvanceAndBroadcast), so the client first applies the
    // replicated CubePlaced (material from the support cell, Dirt on the floor) and then the
    // accepted build's WorldMutationResult. ClientAuthoritativeActionApplier turns that result into
    // a WorldMutationDelta without MaterialId or ActorId, and the CubePlaced branch of
    // RemoteWorldMutationApplier defaults the material to Grass, finds the Dirt cube "wrong",
    // removes it and adds a Grass cube with no replicated id.
    [Fact(Skip = "bug: ClientAuthoritativeActionApplier re-places an accepted build as a Grass cube after the snapshot placed it with its real material (wave 7 tester report)")]
    public void Accepted_Build_Result_After_Its_Replicated_Placement_Keeps_The_Material_And_Id()
    {
        using ClientWorldSession clientWorld = new(16, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        (int x, int y, int z) = FindFreeRaisedCell(clientWorld.Map);
        Vector3 cell = new(x, y, z);
        const int ActorId = 4242;
        ulong checksumBefore = WorldStateChecksum.Compute(clientWorld.Map);

        RemoteWorldMutationApplier.ApplyWorldMutations(
            clientWorld.Map,
            [new WorldMutationDelta(WorldMutationKind.CubePlaced, cell, MaterialId: GeometryMaterialId.Dirt, Sequence: 1, ActorId: ActorId)]);
        Assert.Equal(GeometryMaterialId.Dirt, clientWorld.Map.TryGetGeometryCellAtCell(x, y, z)?.Definition.MaterialId);
        ulong replicatedChecksum = WorldStateChecksum.Compute(clientWorld.Map);
        Assert.NotEqual(checksumBefore, replicatedChecksum);

        ClientAuthoritativeActionApplier.ApplyActionResult(
            clientWorld,
            new SceneEventHub(),
            new AuthoritativeActionResult(
                "w7-session",
                ActionSequence: 7,
                ServerTick: 10,
                Accepted: true,
                Payload: new WorldMutationResult(WorldMutationKind.CubePlaced, cell, Applied: true)),
            "w7-session");

        Assert.Equal(GeometryMaterialId.Dirt, clientWorld.Map.TryGetGeometryCellAtCell(x, y, z)?.Definition.MaterialId);
        Assert.True(clientWorld.Map.TryGetCubeByReplicatedId(ActorId, out Cube? byId) && byId != null);
        Assert.Equal(replicatedChecksum, WorldStateChecksum.Compute(clientWorld.Map));
    }

    // The other arrival order is already fine: the replicated delta carries the material and wins.
    [Fact]
    public void Accepted_Build_Result_Before_Its_Replicated_Placement_Ends_With_The_Replicated_Material()
    {
        using ClientWorldSession clientWorld = new(16, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        (int x, int y, int z) = FindFreeRaisedCell(clientWorld.Map);
        Vector3 cell = new(x, y, z);

        ClientAuthoritativeActionApplier.ApplyActionResult(
            clientWorld,
            new SceneEventHub(),
            new AuthoritativeActionResult("w7-session", 7, 10, Accepted: true, Payload: new WorldMutationResult(WorldMutationKind.CubePlaced, cell, Applied: true)),
            "w7-session");
        RemoteWorldMutationApplier.ApplyWorldMutations(
            clientWorld.Map,
            [new WorldMutationDelta(WorldMutationKind.CubePlaced, cell, MaterialId: GeometryMaterialId.Dirt, Sequence: 1, ActorId: 4242)]);

        Assert.Equal(GeometryMaterialId.Dirt, clientWorld.Map.TryGetGeometryCellAtCell(x, y, z)?.Definition.MaterialId);
        Assert.True(clientWorld.Map.TryGetCubeByReplicatedId(4242, out _));
    }

    private static (int X, int Y, int Z) FindFreeRaisedCell(Map map)
    {
        for (int y = 3; y >= 1; y--)
        {
            for (int x = 1; x < map.Width - 1; x++)
            {
                for (int z = 1; z < map.Depth - 1; z++)
                {
                    if (map.TryGetCubeAtCell(x, y, z) == null && map.TryGetGeometryCellAtCell(x, y, z) == null)
                    {
                        return (x, y, z);
                    }
                }
            }
        }

        throw new InvalidOperationException("No free raised cell.");
    }

    private static string DescribeDiff(Map serverMap, Map clientMap)
    {
        // A diagnostic read of the live server map; racy, but only used to describe a failure.
        HashSet<string> serverCells = DescribeCells(serverMap);
        HashSet<string> clientCells = DescribeCells(clientMap);
        StringBuilder text = new();
        text.Append("server-only=[").AppendJoin(", ", serverCells.Except(clientCells)).Append("] ");
        text.Append("client-only=[").AppendJoin(", ", clientCells.Except(serverCells)).Append("] ");
        text.Append("server cubes=[").AppendJoin(", ", serverMap.SolidCubes.Where(c => c.Position.Y > 1f).Select(c => c.Position.ToString())).Append("] ");
        text.Append("client cubes=[").AppendJoin(", ", clientMap.SolidCubes.Where(c => c.Position.Y > 1f).Select(c => c.Position.ToString())).Append(']');
        return text.ToString();
    }

    private static HashSet<string> DescribeCells(Map map)
    {
        HashSet<string> cells = [];
        foreach (GeometryCellRecord record in map.GeometryCells.Values.Concat(map.RubbleCells.Values).ToArray())
        {
            if (record.Key.Y <= 0)
            {
                continue;
            }

            string kind = record.FracturePieces != null ? "frac"
                : record.SubdivisionState == null && record.Definition.ShapeKind == GeometryShapeKind.SolidCube ? "solid"
                : "other";
            cells.Add($"{record.Key.X},{record.Key.Y},{record.Key.Z}:{kind}:{record.Definition.MaterialId}");
        }

        return cells;
    }
}
