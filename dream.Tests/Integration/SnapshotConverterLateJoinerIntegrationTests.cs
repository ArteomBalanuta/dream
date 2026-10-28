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
using static Dream.Tests.Integration.SnapshotConverterLoopbackSessionIntegrationTests;

namespace Dream.Tests.Integration;

// SnapshotConverterLoopbackSessionIntegrationTests' late-joiner case: a client joining after a
// generated cube fractured builds the server's world, and a resync keeps it. Its own class so it
// runs in parallel with the three-client session; the helpers are the main class's.
public class SnapshotConverterLateJoinerIntegrationTests(ITestOutputHelper output)
{
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
}
