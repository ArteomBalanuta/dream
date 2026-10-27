using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Replication;
using Dream.Client.World;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Physics;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Client.Replication;

// World replication used to identify cubes only by cell, coalesce nothing, never resync, and
// let per-client rubble diverge player physics.
public class WorldReplicationIdentityTests
{
    private const int FloorSize = 12;

    [Fact]
    public void CubeMoved_ResolvesByActorIdNotByWhateverCubeIsInTheSourceCell()
    {
        Map map = new(loadVisualAssets: false, width: 10, depth: 10);
        Cube a = Assert.IsType<Cube>(map.TryAddCube(new Vector3(3f, 2f, 3f), withPhysics: false));
        Cube b = Assert.IsType<Cube>(map.TryAddCube(new Vector3(4f, 2f, 3f), withPhysics: false));
        map.AssignReplicatedCubeId(a, 10);
        map.AssignReplicatedCubeId(b, 11);

        // The server thinks cube 10 is in cell 4; the client has cube 11 there.
        RemoteWorldMutationApplier.ApplyWorldMutations(
            map,
            [WorldMutationDelta.Moved(new Vector3(4f, 2f, 3f), new Vector3(3.5f, 2.5f, 5.5f)) with { ActorId = 10 }]);

        Assert.Same(a, map.TryGetCubeAtCell(3, 2, 5));
        Assert.Same(b, map.TryGetCubeAtCell(4, 2, 3));
    }

    [Fact]
    public void CubeDestroyed_ResolvesByActorId()
    {
        Map map = new(loadVisualAssets: false, width: 10, depth: 10);
        Cube a = Assert.IsType<Cube>(map.TryAddCube(new Vector3(3f, 2f, 3f), withPhysics: false));
        Cube b = Assert.IsType<Cube>(map.TryAddCube(new Vector3(4f, 2f, 3f), withPhysics: false));
        map.AssignReplicatedCubeId(a, 10);

        RemoteWorldMutationApplier.ApplyWorldMutations(
            map,
            [new WorldMutationDelta(WorldMutationKind.CubeDestroyed, new Vector3(4f, 2f, 3f), ActorId: 10)]);

        Assert.Null(map.TryGetCubeAtCell(3, 2, 3));
        Assert.Same(b, map.TryGetCubeAtCell(4, 2, 3));
        Assert.False(map.TryGetCubeByReplicatedId(10, out _));
    }

    [Fact]
    public void CoalesceSameCellMoves_DropsSupersededSameCellMovesAndKeepsCrossCellOrder()
    {
        WorldMutationDelta Same(int id, ulong seq) => new(
            WorldMutationKind.CubeMoved, new Vector3(1f, 1f, 1f), new Vector3(1f, 1f, 1f), new Vector3(1.5f, 1.5f, 1.5f), Sequence: seq, ActorId: id);
        WorldMutationDelta Cross(int id, ulong seq) => new(
            WorldMutationKind.CubeMoved, new Vector3(1f, 1f, 1f), new Vector3(2f, 1f, 1f), new Vector3(2.1f, 1.5f, 1.5f), Sequence: seq, ActorId: id);

        WorldMutationDelta[] coalesced = WorldSessionRuntime.CoalesceSameCellMoves(
            [Same(7, 1), Same(8, 2), Same(9, 3), Same(7, 4), Cross(8, 5), Same(7, 6)]);

        // 7: only its last same-cell move survives. 8: its same-cell move is superseded by the
        // later cross-cell move. 9: nothing later, so it is kept.
        Assert.Equal([3ul, 5ul, 6ul], coalesced.Select(mutation => mutation.Sequence).ToArray());
    }

    [Fact]
    public void ServerAndClient_AgreeOnChecksum_AfterJoinAndReplicatedChanges()
    {
        using WorldSessionRuntime server = CreateServer(out RuntimeAuthoritativeSessionHost host);
        using (host)
        {
            PlayerSessionId session = new("session-a");
            JoinAcceptedMessage accepted = host.JoinSession(session.Value, "A");
            using ClientWorldSession client = CreateClient();
            ClientJoinAcceptedApplier.ApplyToRuntime(client, accepted);

            Assert.True(server.ProcessAuthoritativeAction(new PlayerActionMessage(session, 1, new BuildUseIntent(new Vector3(7f, 1f, 5f)))).Accepted);
            server.AdvanceAuthoritativeTick(WorldSessionRuntime.WorldChecksumIntervalTicks, SimulationClock.FixedDeltaSeconds, (PlayerCommandFrame?)null, PlayerSimulationTuning.AuthoritativeDefault);
            ServerSnapshotEnvelope snapshot = server.ExportServerSnapshotEnvelope();
            RemoteWorldMutationApplier.ApplySnapshotToRuntime(client, snapshot);

            Assert.NotNull(snapshot.WorldChecksum);
            Assert.Contains(snapshot.WorldMutations, mutation => mutation.Kind == WorldMutationKind.CubePlaced && mutation.ActorId.HasValue);
            Assert.False(RemoteWorldMutationApplier.IsWorldOutOfSync(client, snapshot));
            Assert.Equal(WorldStateChecksum.Compute(server.Map), WorldStateChecksum.Compute(client.Map));
        }
    }

    [Fact]
    public void DivergedClient_IsDetectedAndRepairedByResyncWithoutDrainingOtherClientsDeltas()
    {
        using WorldSessionRuntime server = CreateServer(out RuntimeAuthoritativeSessionHost host);
        using (host)
        {
            PlayerSessionId session = new("session-a");
            JoinAcceptedMessage accepted = host.JoinSession(session.Value, "A");
            using ClientWorldSession client = CreateClient();
            ClientJoinAcceptedApplier.ApplyToRuntime(client, accepted);
            Assert.True(server.ProcessAuthoritativeAction(new PlayerActionMessage(session, 1, new BuildUseIntent(new Vector3(7f, 1f, 5f)))).Accepted);
            server.AdvanceAuthoritativeTick(WorldSessionRuntime.WorldChecksumIntervalTicks, SimulationClock.FixedDeltaSeconds, (PlayerCommandFrame?)null, PlayerSimulationTuning.AuthoritativeDefault);

            // The client misses the placement (as if a delta were dropped).
            ServerSnapshotEnvelope snapshot = server.ExportServerSnapshotEnvelope() with { WorldMutations = [] };
            RemoteWorldMutationApplier.ApplySnapshotToRuntime(client, snapshot);
            Assert.True(RemoteWorldMutationApplier.IsWorldOutOfSync(client, snapshot));

            Assert.True(server.ProcessAuthoritativeAction(new PlayerActionMessage(session, 2, new BuildUseIntent(new Vector3(7f, 1f, 6f)))).Accepted);
            ServerSnapshotEnvelope resync = host.ExportWorldResync();
            RemoteWorldMutationApplier.ApplySnapshotToRuntime(client, resync);

            Assert.Equal(WorldStateChecksum.Compute(server.Map), WorldStateChecksum.Compute(client.Map));
            // The second placement is still pending for everyone else.
            ServerSnapshotEnvelope next = server.ExportServerSnapshotEnvelope();
            Assert.Contains(next.WorldMutations, mutation => mutation.Kind == WorldMutationKind.CubePlaced);
            // And the resynced client does not replay it on top of the world it already has.
            int cubesBefore = client.Map.SolidCubes.Count;
            RemoteWorldMutationApplier.ApplySnapshotToRuntime(client, next);
            Assert.Equal(cubesBefore, client.Map.SolidCubes.Count);
        }
    }

    [Fact]
    public void ResyncRequest_RoundTripsThroughTheCodec()
    {
        TransportJsonCodec codec = new();
        ClientWorldResyncRequestMessage request = new(1234UL);

        byte[] payload = codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, request.MessageKind, 1), request);

        Assert.Equal(request, Assert.IsType<ClientWorldResyncRequestMessage>(codec.Decode(payload).Message));
    }

    [Fact]
    public void ResyncHandler_AnswersJoinedConnectionsOnly()
    {
        using WorldSessionRuntime server = CreateServer(out RuntimeAuthoritativeSessionHost host);
        using (host)
        {
            ServerRoutingContext context = new("connection-a", host, new LatestPlayerCommandInbox(), new PlayerActionQueue());
            WorldResyncRequestHandler handler = new();

            handler.Handle(context, new ClientWorldResyncRequestMessage(1));
            Assert.Empty(context.OutboundMessages);

            context.LastJoinRequest = new ClientJoinRequestMessage("session-a", "A");
            handler.Handle(context, new ClientWorldResyncRequestMessage(1));
            ServerSnapshotMessage reply = Assert.IsType<ServerSnapshotMessage>(Assert.Single(context.OutboundMessages));
            Assert.NotNull(reply.Snapshot.World);
        }
    }

    [Fact]
    public void FracturePieces_UseDebrisGroupThatPlayersIgnore()
    {
        using PhysicsIntegrator physics = new();
        Map map = new(physics, loadVisualAssets: false, width: 10, depth: 10);
        map.AddCube(new Vector3(3f, 1f, 3f));
        map.FractureCubeIntoPieces(new GeometryCellKey(3, 1, 3));

        BulletSharp.RigidBody piece = physics.DynamicMapCubeBodies.First(
            body => body.UserObject is GeometryDynamicBodyReference);

        Assert.Equal((int)PhysicsIntegrator.DebrisCollisionGroup, (int)piece.BroadphaseProxy.CollisionFilterGroup);
        Assert.Equal(0, (int)piece.BroadphaseProxy.CollisionFilterMask & (int)PhysicsIntegrator.PlayerCollisionGroup);
        Assert.Equal(0, (int)PhysicsIntegrator.PlayerCollisionMask & (int)PhysicsIntegrator.DebrisCollisionGroup);
    }

    private static WorldSessionRuntime CreateServer(out RuntimeAuthoritativeSessionHost host)
    {
        WorldSessionRuntime runtime = new(FloorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub(), loadVisualAssets: false);
        runtime.Enemies.Clear();
        host = new RuntimeAuthoritativeSessionHost(runtime);
        return runtime;
    }

    private static ClientWorldSession CreateClient()
    {
        return new ClientWorldSession(FloorSize, new Vector3(5f, 1.5f, 5f), new SceneEventHub(), loadVisualAssets: false);
    }
}
