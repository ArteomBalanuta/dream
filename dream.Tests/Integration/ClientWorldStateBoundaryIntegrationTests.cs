using System.Diagnostics;
using System.Reflection;
using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Networking;
using Dream.Client.Replication;
using Dream.Client.Scenes;
using Dream.Client.Startup;
using Dream.Client.World;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Tests.Integration;

// DEC-7 took IClientWorldState (and its replication-ack state) off the authoritative
// WorldSessionRuntime. The client must still get all of its world state through
// ClientWorldSession, which is now the interface's only implementation.
public class ClientWorldStateBoundaryIntegrationTests
{
    [Fact]
    public void Authoritative_Runtime_No_Longer_Implements_Client_World_State()
    {
        Assert.False(typeof(IClientWorldState).IsAssignableFrom(typeof(WorldSessionRuntime)));
        Assert.Null(typeof(WorldSessionRuntime).GetProperty(nameof(IClientWorldState.LastAppliedReplicatedWorldMutationSequence)));
        Assert.Null(typeof(WorldSessionRuntime).GetMethod(nameof(IClientWorldState.AcknowledgeReplicatedWorldMutations)));
    }

    [Fact]
    public void Client_World_Session_Is_The_Only_Client_World_State_And_PlayScene_Holds_It()
    {
        Type[] implementations = typeof(IClientWorldState).Assembly
            .GetTypes()
            .Where(type => !type.IsInterface && typeof(IClientWorldState).IsAssignableFrom(type))
            .ToArray();
        Assert.Equal([typeof(ClientWorldSession)], implementations);

        // PlayScene's world is a ClientWorldSession, and it keeps no authoritative runtime.
        FieldInfo[] fields = typeof(PlayScene).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.Contains(fields, field => field.FieldType == typeof(ClientWorldSession));
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(WorldSessionRuntime));
        Assert.DoesNotContain(fields, field => field.FieldType == typeof(IClientWorldState));
    }

    [Fact]
    public async Task Loopback_Client_Gets_Join_Snapshots_And_A_Built_Cube_Through_Client_World_Session()
    {
        const string SessionId = "dec7-session";
        DedicatedServerHost? host = null;
        await using IntegratedLocalServerHandle server = await IntegratedLocalServerHandle.StartAsync(
            0,
            () => host = DedicatedServerHost.CreateRuntimeAttached(persistence: null));
        Assert.True(server.IsReady, server.ErrorMessage);
        int floorSize = host!.Runtime!.Map.Width;

        using ClientWorldSession clientWorld = new(floorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        // Every applier below sees only the client-side interface.
        IClientWorldState world = clientWorld;
        int placedBefore = clientWorld.Map.SolidCubes.Count(cube => cube.Position.Y > 1f);

        await using RemoteClientSession client = await RemoteClientSession.ConnectAsync(
            new RemoteClientOptions("127.0.0.1", server.BoundPort, SessionId, "Dec Seven", "dec7-test"));

        // A safety deadline only: the loop exits as soon as every condition below has been seen.
        TimeSpan deadline = TimeSpan.FromSeconds(30);
        Stopwatch clock = Stopwatch.StartNew();
        bool joined = false;
        int snapshots = 0;
        int checksumsVerified = 0;
        int checksumMismatches = 0;
        Vector3? serverPosition = null;
        ulong commandSequence = 0;
        bool buildsSent = false;
        int buildFrames = 0;
        int acceptedBuilds = 0;
        ulong lastBuildCommandSequence = 0;
        int snapshotsAtLastBuildAck = -1;
        SceneEventHub clientEvents = new();
        // Build use reaches the server only as command-frame primary use (NETR-6 rejects
        // BuildUseIntent over player.action). The client holds primary use in Building mode while
        // turning through these facings; each placement lands CubePlacementDistance ahead of its eye.
        // Tilted up, like the old wire targets above the floor, so no placement rests on the floor.
        Vector3[] buildFacings =
        [
            new Vector3(1f, 0.75f, 0f).Normalized(), new Vector3(-1f, 0.75f, 0f).Normalized(),
            new Vector3(0f, 0.75f, 1f).Normalized(), new Vector3(0f, 0.75f, -1f).Normalized()
        ];
        const int FramesPerFacing = 10;
        while (clock.Elapsed < deadline)
        {
            commandSequence++;
            PlayerSessionId session = new(SessionId);
            bool buildFrame = buildsSent && buildFrames < buildFacings.Length * FramesPerFacing;
            if (buildFrame)
            {
                lastBuildCommandSequence = commandSequence;
            }

            PlayerCommandFrame frame = buildFrame
                ? new PlayerCommandFrame(
                    session,
                    commandSequence,
                    new PlayerModeSwitchIntent(PlayerInteractionMode.Building),
                    PlayerMoveIntent.None,
                    new PlayerLookIntent(0f, 0f, buildFacings[buildFrames++ / FramesPerFacing]),
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
                    case ServerActionMessage actionMessage:
                        ClientAuthoritativeActionApplier.ApplyActionResult(world, clientEvents, actionMessage.Result, SessionId);
                        if (actionMessage.Result.Accepted && actionMessage.Result.Payload is WorldMutationResult { Applied: true })
                        {
                            acceptedBuilds++;
                        }

                        break;
                    case ServerSnapshotMessage snapshotMessage:
                        snapshots++;
                        ServerSnapshotEnvelope snapshot = snapshotMessage.Snapshot;
                        RemoteWorldMutationApplier.ApplySnapshotToRuntime(world, snapshot);
                        if (snapshot.WorldChecksum.HasValue)
                        {
                            checksumsVerified++;
                            if (RemoteWorldMutationApplier.IsWorldOutOfSync(world, snapshot))
                            {
                                checksumMismatches++;
                            }
                        }

                        ActorSnapshot self = snapshot.Actors.FirstOrDefault(actor => actor.Kind == CreatureKind.Player && actor.SessionId == SessionId);
                        if (self.SessionId == SessionId)
                        {
                            serverPosition = self.Position;
                        }

                        // The server accepts a command frame in the tick that simulates it, so once a
                        // snapshot acknowledges the last build frame every build has been attempted.
                        if (snapshotsAtLastBuildAck < 0 && lastBuildCommandSequence > 0 &&
                            buildFrames >= buildFacings.Length * FramesPerFacing &&
                            snapshot.LastAcceptedCommandSequences != null &&
                            snapshot.LastAcceptedCommandSequences.TryGetValue(SessionId, out ulong acked) &&
                            acked >= lastBuildCommandSequence)
                        {
                            snapshotsAtLastBuildAck = snapshots;
                        }

                        break;
                }
            }

            if (!buildsSent && serverPosition.HasValue && snapshots > 5)
            {
                // Any accepted placement makes the server replicate a CubePlaced mutation to this
                // client; the build frames above start on the next iteration.
                buildsSent = true;
            }

            // Exit on progress, not on elapsed time: every build frame has been simulated and answered
            // (a later snapshot followed the one acknowledging the last build frame, so no accepted
            // build can still arrive), the accepted ones are all replicated into the client world,
            // and enough snapshots and checksums have been seen for the assertions below.
            if (buildsSent && buildFrames >= buildFacings.Length * FramesPerFacing &&
                snapshotsAtLastBuildAck >= 0 && snapshots > snapshotsAtLastBuildAck &&
                acceptedBuilds > 0 &&
                clientWorld.Map.SolidCubes.Count(cube => cube.Position.Y > 1f) == placedBefore + acceptedBuilds &&
                clientWorld.LastAppliedReplicatedWorldMutationSequence > 0 &&
                snapshots > 10 &&
                checksumsVerified > 3)
            {
                break;
            }

            await Task.Delay(15);
        }

        Assert.True(joined, "The join was never accepted.");
        Assert.True(snapshots > 10, $"Only {snapshots} snapshots arrived.");
        Assert.True(serverPosition.HasValue, "No snapshot carried this client's player.");
        Assert.True(buildsSent);
        Assert.True(
            clientWorld.LastAppliedReplicatedWorldMutationSequence > 0,
            "No replicated world mutation was acknowledged by the client world session.");
        Assert.True(acceptedBuilds > 0, "The server accepted none of the build actions.");
        Assert.Equal(placedBefore + acceptedBuilds, clientWorld.Map.SolidCubes.Count(cube => cube.Position.Y > 1f));
        Assert.True(checksumsVerified > 0, "No world checksum was verified.");
        Assert.Equal(0, checksumMismatches);
    }
}
