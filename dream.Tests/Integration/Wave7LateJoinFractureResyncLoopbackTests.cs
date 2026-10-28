using System.Diagnostics;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Replication;
using Dream.Client.World;
using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Integration;

// Wave 7 tester: NETR-6 defers world resyncs to the end of the tick and limits them to one per
// 1.5 s per connection. The wave-6 resync loop for fractured terrain must still converge: a late
// joiner that generated the terrain itself, joining after a terrain cube fractured, reaches the
// server checksum (directly or through a deferred resync) and stays there.
public class Wave7LateJoinFractureResyncLoopbackTests(ITestOutputHelper output)
{
    private const string Fighter = "w7-fighter";
    private const string LateJoiner = "w7-late";

    [Fact]
    public async Task LateJoiner_AfterTerrainFracture_ConvergesOnTheServerChecksum_WithinBoundedTicks()
    {
        await using WireLoopbackServer server = await WireLoopbackServer.StartAsync(
            DedicatedServerHost.CreateRuntimeAttached(persistence: null));
        await using WireLoopbackClient fighter = await server.ConnectAndJoinAsync(Fighter);
        server.Runtime.Enemies.Clear();

        // Attack the nearest raised terrain cube from the fighter's eye until it fractures.
        Map serverMap = server.Runtime.Map;
        Vector3 eye = server.Runtime.Player.Camera.Position;
        Cube target = serverMap.SolidCubes
            .Where(cube => cube.Position.Y > 1f && serverMap.TryGetCellForCube(cube, out _))
            .OrderBy(cube => (cube.Position - eye).LengthSquared)
            .First();
        Assert.True(serverMap.TryGetCellForCube(target, out GeometryCellKey targetCell));
        int fractureTick = -1;
        for (int tick = 0; tick < 1200 && fractureTick < 0; tick++)
        {
            Vector3 aim = (target.Position - server.Runtime.Player.Camera.Position).Normalized();
            await fighter.SendCommandAsync(PlayerInteractionMode.Combat, PlayerMoveIntent.None, aim, attack: true);
            await server.TickAsync();
            Assert.True(await server.FlushAllAsync());
            if (serverMap.TryGetRubbleAtCell(targetCell) != null)
            {
                fractureTick = tick;
            }
        }

        Assert.True(fractureTick >= 0, $"terrain cube at {targetCell} never fractured");
        for (int tick = 0; tick < 30; tick++)
        {
            await fighter.SendCommandAsync(PlayerInteractionMode.Building, PlayerMoveIntent.None, Vector3.UnitZ, attack: false);
            await server.TickAsync();
        }

        Assert.True(await server.FlushAllAsync());

        // The late joiner builds its terrain itself, as a real client does, then applies the wire.
        await using WireLoopbackClient late = await server.ConnectAndJoinAsync(LateJoiner);
        using ClientWorldSession world = new(serverMap.Width, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        int frameIndex = 0;
        WorldTransferReassembler transfers = new();
        List<IServerToClientMessage> delivered = [];
        Stopwatch lastRequest = new();
        PhaseResult joinPhase = await RunUntilStableAsync();
        output.WriteLine($"fracture at tick {fractureTick}; join phase: {joinPhase}");
        Assert.True(world.Map.TryGetRubbleAtCell(targetCell) != null, "the late joiner has no rubble in the fractured cell");
        Assert.Null(world.Map.TryGetCubeAtCell(targetCell.X, targetCell.Y, targetCell.Z));
        Assert.True(joinPhase.FirstMatchTick.HasValue, $"never matched the server checksum: {joinPhase}");
        Assert.Equal(0, joinPhase.MismatchesAfterFirstMatch);

        // Now diverge on purpose (a stray local cube) and send a second request inside the server's
        // 1.5 s cooldown on top of the first: the deferred resync must still heal the client.
        (int x, int y, int z) = FindFreeRaisedCell(world.Map, serverMap);
        Assert.NotNull(world.Map.TryAddCube(new Vector3(x, y, z), withPhysics: false));
        lastRequest.Reset();
        PhaseResult divergedPhase = await RunUntilStableAsync(extraRequestAfterFirst: true);
        output.WriteLine($"diverged phase: {divergedPhase}");
        Assert.True(divergedPhase.Requests >= 1);
        Assert.True(divergedPhase.ResyncSnapshots >= 1, $"no full-world resync arrived: {divergedPhase}");
        Assert.True(divergedPhase.FirstMatchTick.HasValue, $"the resync never healed the client: {divergedPhase}");
        Assert.True(divergedPhase.FirstMatchTick!.Value <= 4 * SimulationClock.AuthoritativeTicksPerSecond, divergedPhase.ToString());
        Assert.Equal(0, divergedPhase.MismatchesAfterFirstMatch);
        Assert.Null(world.Map.TryGetCubeAtCell(x, y, z));

        async Task<PhaseResult> RunUntilStableAsync(bool extraRequestAfterFirst = false)
        {
            int checksums = 0;
            int mismatches = 0;
            int requests = 0;
            int resyncSnapshots = 0;
            int? firstMatchTick = null;
            int mismatchesAfterFirstMatch = 0;
            const int MaxTicks = 8 * SimulationClock.AuthoritativeTicksPerSecond;
            for (int tick = 0; tick < MaxTicks; tick++)
            {
                await fighter.SendCommandAsync(PlayerInteractionMode.Building, PlayerMoveIntent.None, Vector3.UnitZ, attack: false);
                await late.SendCommandAsync(PlayerInteractionMode.Building, PlayerMoveIntent.None, Vector3.UnitZ, attack: false);
                await server.TickAsync();
                Assert.True(await server.FlushAllAsync());
                await late.WaitForSnapshotTickAsync(server.Runtime.CurrentServerTick);

                IReadOnlyList<WireFrame> frames = late.Frames;
                for (; frameIndex < frames.Count; frameIndex++)
                {
                    // Protocol v3: a join or resync world arrives in chunks; the reassembler hands the
                    // messages on in the order RemoteClientSession delivers them.
                    delivered.Clear();
                    transfers.Accept((IServerToClientMessage)frames[frameIndex].Decoded.Message, delivered.Add);
                    foreach (IServerToClientMessage message in delivered)
                    {
                        switch (message)
                        {
                            case JoinAcceptedMessage accepted:
                                ClientJoinAcceptedApplier.ApplyToRuntime(world, accepted);
                                break;
                            case ServerSnapshotMessage { Snapshot: var snapshot }:
                                if (snapshot.World != null)
                                {
                                    resyncSnapshots++;
                                }

                                RemoteWorldMutationApplier.ApplySnapshotToRuntime(world, snapshot);
                                if (!snapshot.WorldChecksum.HasValue)
                                {
                                    break;
                                }

                                checksums++;
                                if (!RemoteWorldMutationApplier.IsWorldOutOfSync(world, snapshot))
                                {
                                    firstMatchTick ??= tick;
                                    break;
                                }

                                mismatches++;
                                if (firstMatchTick.HasValue)
                                {
                                    mismatchesAfterFirstMatch++;
                                }

                                // PlayScene's own 2 s request cooldown.
                                if (!lastRequest.IsRunning || lastRequest.Elapsed.TotalSeconds >= 2d)
                                {
                                    lastRequest.Restart();
                                    requests++;
                                    await late.SendAsync(new ClientWorldResyncRequestMessage(WorldStateChecksum.Compute(world.Map)));
                                    if (extraRequestAfterFirst && requests == 1)
                                    {
                                        // Inside the server cooldown: dropped, must not cancel the first.
                                        await late.SendAsync(new ClientWorldResyncRequestMessage(WorldStateChecksum.Compute(world.Map)));
                                    }

                                    await Task.Delay(20);
                                }

                                break;
                        }
                    }
                }

                if (firstMatchTick.HasValue && checksums >= 3 && tick - firstMatchTick.Value > SimulationClock.AuthoritativeTicksPerSecond)
                {
                    break;
                }
            }

            return new PhaseResult(checksums, mismatches, requests, resyncSnapshots, firstMatchTick, mismatchesAfterFirstMatch);
        }
    }

    private static (int X, int Y, int Z) FindFreeRaisedCell(Map clientMap, Map serverMap)
    {
        for (int x = 2; x < clientMap.Width - 2; x++)
        {
            for (int z = 2; z < clientMap.Depth - 2; z++)
            {
                if (clientMap.TryGetGeometryCellAtCell(x, 4, z) == null && serverMap.TryGetGeometryCellAtCell(x, 4, z) == null)
                {
                    return (x, 4, z);
                }
            }
        }

        throw new InvalidOperationException("No free cell at y = 4.");
    }

    private readonly record struct PhaseResult(
        int Checksums,
        int Mismatches,
        int Requests,
        int ResyncSnapshots,
        int? FirstMatchTick,
        int MismatchesAfterFirstMatch);
}
