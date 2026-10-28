using System.Text;
using System.Text.Json;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Replication;
using Dream.Client.World;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Multiplayer.Protocol;

namespace Dream.Tests.Integration;

// WIRE-11 (hand-written snapshot converters) with WIRE-10 (command converters) and WIRE-5 (v2
// slimming), end to end: the production authoritative host (deferred joins) behind the real
// DedicatedServerService over loopback TCP, three clients for 300+ ticks with joins, a leave, a
// rejoin under the same session id, combat until the wolf dies and respawns, a built cube broken
// until it fractures, and a client-requested world resync (a full World snapshot). Every frame
// any client received must decode, and its payload must re-encode through the reflection
// reference (ProtocolWireOptions.V2) byte for byte; every client's replayed world must match the
// server's checksums.
[Collection(TimingSensitiveCollection.Name)]
public class SnapshotConverterLoopbackSessionIntegrationTests(ITestOutputHelper output)
{
    private const int Ticks = 300;
    private const string PrimarySession = "session-a";
    private const string SessionB = "session-b";
    private const string SessionC = "session-c";

    private const int JoinCAt = 20;
    private const int BuildAt = 5;
    private const int LeaveBAt = 120;
    private const int RejoinBAt = 160;
    private const int ResyncAt = 220;

    [Fact]
    public async Task ThreeClients_JoinLeaveRejoinCombatFractureResync_EveryFrameReencodesByteIdentically_AndWorldsMatch()
    {
        await using WireLoopbackServer server = await WireLoopbackServer.StartAsync(ServerBootstrap.CreateAuthoritativeHost(persistence: null));
        await using WireLoopbackClient a = await server.ConnectAndJoinAsync(PrimarySession);
        await using WireLoopbackClient b = await server.ConnectAndJoinAsync(SessionB);
        WireLoopbackClient? c = null;
        WireLoopbackClient? rejoinedB = null;
        List<WireLoopbackClient> everyClient = [a, b];
        Vector3 buildCell = BuildCellBeside(server, PrimarySession);

        bool cubeFractured = false;
        int generatedCellsFracturedBeforeRejoin = 0;
        int iteration = 0;
        try
        {
            while (server.Runtime.CurrentServerTick < Ticks)
            {
                List<WireLoopbackClient> active = [a];
                if (iteration < LeaveBAt)
                {
                    active.Add(b);
                }

                if (rejoinedB != null)
                {
                    active.Add(rejoinedB);
                }

                if (c != null)
                {
                    active.Add(c);
                }

                foreach (WireLoopbackClient client in active)
                {
                    bool breakingCube = client == a && iteration >= BuildAt + 2 && !cubeFractured;
                    Vector3? toEnemy = server.DirectionToNearestEnemy(client.SessionId);
                    Vector3 facing = breakingCube ? Vector3.UnitX : toEnemy ?? Vector3.UnitZ;
                    bool building = client == a && iteration <= BuildAt;
                    await client.SendCommandAsync(
                        building ? PlayerInteractionMode.Building : PlayerInteractionMode.Combat,
                        PlayerMoveIntent.None,
                        facing,
                        attack: !building && (breakingCube || toEnemy.HasValue));
                }

                if (iteration == BuildAt)
                {
                    await a.SendActionAsync(new BuildUseIntent(buildCell));
                }

                if (iteration == ResyncAt && c != null)
                {
                    await c.SendAsync(new ClientWorldResyncRequestMessage(0));
                }

                await server.TickAsync();
                Assert.True(await server.FlushAllAsync());
                cubeFractured |= server.Runtime.Map.CaptureFracturePieces().Any(piece => piece.Cell.X == (int)buildCell.X && piece.Cell.Z == (int)buildCell.Z);

                if (iteration == JoinCAt)
                {
                    c = await server.ConnectAndJoinAsync(SessionC);
                    everyClient.Add(c);
                }
                else if (iteration == LeaveBAt)
                {
                    await server.DisconnectAsync(b);
                }
                else if (iteration == RejoinBAt)
                {
                    generatedCellsFracturedBeforeRejoin = GeneratedFracturedCells(server, buildCell);
                    rejoinedB = await server.ConnectAndJoinAsync(SessionB);
                    everyClient.Add(rejoinedB);
                }

                iteration++;
            }

            ulong lastTick = server.Runtime.CurrentServerTick;
            WireLoopbackClient[] connected = [a, c!, rejoinedB!];
            foreach (WireLoopbackClient client in connected)
            {
                await client.WaitForSnapshotTickAsync(lastTick);
            }

            // The session covered what it claims to.
            Assert.True(cubeFractured, "The built cube never fractured.");
            Assert.Contains(c!.Frames, frame => frame.Decoded.Message is ServerSnapshotMessage { Snapshot.World: not null });
            Assert.Contains(a.Frames, frame => frame.Decoded.Message is ServerSnapshotMessage message && message.Snapshot.DamageEvents.Count > 0);
            Assert.Contains(a.Frames, frame => frame.Decoded.Message is ServerSnapshotMessage message &&
                message.Snapshot.WorldMutations.Any(mutation => mutation.Kind == WorldMutationKind.CubeFractured && mutation.FracturePieces?.Count > 0));
            Assert.Contains(a.Frames, frame => frame.Decoded.Message is ServerSnapshotMessage message &&
                message.Snapshot.Actors.Any(actor => actor.Kind == CreatureKind.Enemy && actor.EnemyBehaviorState.HasValue));
            Assert.True(
                a.Frames.Count(frame => frame.Decoded.Message is ServerActionMessage) > 0,
                "No action result reached the primary.");

            Dictionary<string, int> kinds = new(StringComparer.Ordinal);
            int reencoded = 0;
            long bytes = 0;
            foreach (WireLoopbackClient client in everyClient)
            {
                Assert.Null(client.ReadFault);
                foreach (WireFrame frame in client.Frames)
                {
                    IProtocolMessage message = frame.Decoded.Message;
                    byte[] reference = JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), ProtocolWireOptions.V2);
                    Assert.True(
                        reference.AsSpan().SequenceEqual(frame.PayloadJson),
                        $"{client.SessionId}: a {frame.Decoded.Envelope.MessageKind} frame (sequence {frame.Decoded.Envelope.Sequence}) " +
                        $"does not re-encode byte for byte.\nwire: {Encoding.UTF8.GetString(frame.PayloadJson)}\nref:  {Encoding.UTF8.GetString(reference)}");
                    kinds[frame.Decoded.Envelope.MessageKind] = kinds.GetValueOrDefault(frame.Decoded.Envelope.MessageKind) + 1;
                    reencoded++;
                    bytes += frame.Payload.Length;
                }
            }

            Assert.True(kinds.ContainsKey("server.hello"));
            Assert.True(kinds.ContainsKey("server.join-accepted"));
            Assert.True(kinds.ContainsKey("server.snapshot"));
            Assert.True(kinds.ContainsKey("server.action"));

            // Every client, the one that left included, rebuilt the server's world from its own
            // stream; the connected ones end on the server's final world.
            ulong serverChecksum = WorldStateChecksum.Compute(server.Runtime.Map);
            foreach (WireLoopbackClient client in everyClient)
            {
                ClientWorldReplay replay = Replay(client, server.Runtime.Map.Width);
                output.WriteLine($"{client.SessionId}: frames {client.Frames.Count}, checksums verified {replay.ChecksumsVerified}, mutations {replay.MutationsApplied}");
                // Checksums go out periodically; the client that left saw about 120 ticks of them.
                int expectedChecksums = client == b ? 1 : 2;
                Assert.True(replay.ChecksumsVerified >= expectedChecksums, $"{client.SessionId} verified {replay.ChecksumsVerified} checksums.");
                if (client == rejoinedB)
                {
                    // A client that joins after generated terrain fractured must drop that terrain
                    // cube (see LateJoiner_AfterAGeneratedCubeFractured_BuildsTheServersWorld).
                    output.WriteLine(
                        $"{client.SessionId} (rejoined) joined after {generatedCellsFracturedBeforeRejoin} generated cells fractured: " +
                        $"{replay.ChecksumMismatches} checksum mismatches");
                }

                Assert.True(
                    replay.ChecksumMismatches == 0,
                    $"{client.SessionId}: {replay.ChecksumMismatches} checksum mismatches " +
                    $"({generatedCellsFracturedBeforeRejoin} generated cells fractured before the rejoin).");
                if (client != b)
                {
                    Assert.Equal(serverChecksum, replay.FinalChecksum);
                }
            }

            output.WriteLine(
                $"ticks {lastTick}, iterations {iteration}, {reencoded} frames ({bytes} bytes) re-encoded identically: " +
                string.Join(", ", kinds.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}")));
        }
        finally
        {
            if (c != null)
            {
                await c.DisposeAsync();
            }

            if (rejoinedB != null)
            {
                await rejoinedB.DisposeAsync();
            }
        }
    }

    // Regression for a production bug (pre-existing, not from wave 6, fixed in wave 6 fix loop 1):
    // a client that joins (or resyncs into a fresh world) after the server fractured a GENERATED
    // terrain cube kept that cube. The client builds the generated startup terrain itself, and
    // RemoteWorldMutationApplier.ApplyWorldSnapshot used to treat the snapshot's fractured cells as
    // cells that may keep a cube. Map.RestoreFracturePieces no longer removes a cube in the origin
    // cell, so the client ended with both the intact cube and the rubble, its WorldStateChecksum
    // never matched the server's, and the resync (same path) never healed it.
    [Fact]
    public void LateJoiner_AfterAGeneratedCubeFractured_BuildsTheServersWorld_AndAResyncKeepsIt()
    {
        AssertLateJoinerMatchesAfterGeneratedFracture();
    }

    private void AssertLateJoinerMatchesAfterGeneratedFracture()
    {
        using DedicatedServerHost host = ServerBootstrap.CreateAuthoritativeHost(persistence: null);
        WorldSessionRuntime runtime = host.Runtime!;
        Assert.Empty(host.HandleClientMessage("connection-a", new ClientJoinRequestMessage(PrimarySession, "Name A")));
        AdvanceOneTick(host);
        _ = host.CreateSnapshotMessage();

        // The primary breaks the generated terrain cube on its +X ray (terrain starts at x = 7).
        ulong sequence = 0;
        for (int tick = 0; tick < 600 && !runtime.Map.CaptureFracturePieces().Any(); tick++)
        {
            host.HandleClientMessage("connection-a", new PlayerCommandMessage(new PlayerCommandFrame(
                new PlayerSessionId(PrimarySession),
                ++sequence,
                new PlayerModeSwitchIntent(PlayerInteractionMode.Combat),
                PlayerMoveIntent.None,
                new PlayerLookIntent(0f, 0f, Vector3.UnitX),
                PrimaryUsePressed: true)));
            AdvanceOneTick(host);
            host.DrainPendingActionResults();
            _ = host.CreateSnapshotMessage();
        }

        GeometryCellKey[] fractured = runtime.Map.CaptureFracturePieces().Select(piece => piece.Cell).Distinct().ToArray();
        Assert.NotEmpty(fractured);
        Assert.All(fractured, cell => Assert.True(cell.X >= 7, $"{cell} is not generated terrain."));

        Assert.Empty(host.HandleClientMessage("connection-b", new ClientJoinRequestMessage(SessionB, "Name B")));
        AdvanceOneTick(host);
        List<PendingConnectionReply> replies = [];
        host.DrainPendingJoinRepliesInto(replies);
        JoinAcceptedMessage accepted = Assert.IsType<JoinAcceptedMessage>(
            Assert.Single(replies, reply => reply.ConnectionId == "connection-b" && reply.Message is JoinAcceptedMessage).Message);
        ulong serverChecksum = WorldStateChecksum.Compute(runtime.Map);

        using ClientWorldSession world = new(runtime.Map.Width, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        ClientJoinAcceptedApplier.ApplyToRuntime(world, accepted);
        output.WriteLine($"fractured {string.Join(", ", fractured)}; intact cube left at a fractured cell: " +
            string.Join(", ", fractured.Where(cell => world.Map.TryGetCubeAtCell(cell.X, cell.Y, cell.Z) != null)));
        Assert.Equal(serverChecksum, WorldStateChecksum.Compute(world.Map));
        Assert.All(fractured, cell => Assert.Null(world.Map.TryGetCubeAtCell(cell.X, cell.Y, cell.Z)));

        // And the full-world resync the client would request lands on the same world.
        RemoteWorldMutationApplier.ApplySnapshotToRuntime(world, runtime.ExportWorldResyncEnvelope());
        Assert.Equal(serverChecksum, WorldStateChecksum.Compute(world.Map));
    }

    private static void AdvanceOneTick(DedicatedServerHost host)
    {
        int executed = host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
        if (executed == 0)
        {
            executed = host.AdvanceTime(SimulationClock.FixedDeltaSeconds * 0.01);
        }

        Assert.Equal(1, executed);
    }

    private static int GeneratedFracturedCells(WireLoopbackServer server, Vector3 buildCell)
    {
        return server.Runtime.Map.CaptureFracturePieces()
            .Select(piece => piece.Cell)
            .Distinct()
            .Count(cell => cell.X != (int)buildCell.X || cell.Y != (int)buildCell.Y || cell.Z != (int)buildCell.Z);
    }

    // On the startup floor generated terrain starts at x = 7 beside the spawn; the cube goes in
    // the free cell between the primary and that terrain, on its +X attack ray.
    private static Vector3 BuildCellBeside(WireLoopbackServer server, string sessionId)
    {
        ActorSnapshot primary = server.Runtime.CaptureActorSnapshots().Single(actor => actor.SessionId == sessionId);
        Vector3 cell = new(MathF.Floor(primary.Position.X) + 1f, 1f, MathF.Floor(primary.Position.Z));
        Assert.Null(server.Runtime.Map.TryGetCubeAtCell((int)cell.X, 1, (int)cell.Z));
        return cell;
    }

    private sealed record ClientWorldReplay(int ChecksumsVerified, int ChecksumMismatches, ulong FinalChecksum, int MutationsApplied);

    // Applies every received message, in order, into a client world like PlayScene does.
    private static ClientWorldReplay Replay(WireLoopbackClient client, int floorSize)
    {
        using ClientWorldSession world = new(floorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        int verified = 0;
        int mismatches = 0;
        int mutations = 0;
        foreach (WireFrame frame in client.Frames)
        {
            switch (frame.Decoded.Message)
            {
                case JoinAcceptedMessage accepted:
                    ClientJoinAcceptedApplier.ApplyToRuntime(world, accepted);
                    break;
                case ServerSnapshotMessage snapshotMessage:
                    ServerSnapshotEnvelope snapshot = snapshotMessage.Snapshot;
                    RemoteWorldMutationApplier.ApplySnapshotToRuntime(world, snapshot);
                    mutations += snapshot.WorldMutations.Count;
                    if (snapshot.WorldChecksum.HasValue)
                    {
                        verified++;
                        mismatches += RemoteWorldMutationApplier.IsWorldOutOfSync(world, snapshot) ? 1 : 0;
                    }

                    break;
            }
        }

        return new ClientWorldReplay(verified, mismatches, WorldStateChecksum.Compute(world.Map), mutations);
    }
}
