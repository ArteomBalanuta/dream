using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;
using Dream.Client.Replication;
using Dream.Client.World;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Runtime;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Integration;

// Wave 12 tester, SCALE-7a headless server map at runtime: the production-shaped server (deferred
// joins, headless runtime map) over loopback with the real RemoteClientSession. Builds, a fracture,
// a world resync and a late join run against the headless map; the client mirrors what it
// receives into a ClientWorldSession (as PlayScene does), whose map must stay a rendering map and
// render the built and fractured cells, while every render export on the server map throws.
public sealed class Wave12HeadlessServerLoopbackTests(ITestOutputHelper output)
{
    [Fact]
    public async Task HeadlessServer_JoinBuildFractureResyncLateJoin_ClientMirrorStillRenders()
    {
        await using Wave12DeltaActorLoopbackTests.Rig rig = await Wave12DeltaActorLoopbackTests.Rig.StartAsync(output);
        Map serverMap = rig.Runtime.Map;
        Assert.True(serverMap.IsHeadless);

        using ClientWorldSession world = new(serverMap.Width, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        Assert.False(world.Map.IsHeadless);
        int resyncs = 0;
        int checksums = 0;
        int outOfSync = 0;
        Wave12DeltaActorLoopbackTests.Peer primary = await rig.ConnectWithoutJoiningAsync(ProtocolV2SharedSnapshotLoopbackIntegrationTests.PrimarySession);
        primary.Observer = message =>
        {
            switch (message)
            {
                case JoinAcceptedMessage accepted:
                    ClientJoinAcceptedApplier.ApplyToRuntime(world, accepted);
                    break;
                case ServerSnapshotMessage { Snapshot: var snapshot }:
                    resyncs += snapshot.World != null ? 1 : 0;
                    RemoteWorldMutationApplier.ApplySnapshotToRuntime(world, snapshot);
                    if (snapshot.WorldChecksum.HasValue)
                    {
                        checksums++;
                        outOfSync += RemoteWorldMutationApplier.IsWorldOutOfSync(world, snapshot) ? 1 : 0;
                    }

                    break;
            }
        };
        await rig.WaitUntilJoinedAsync(primary);

        // Builds through the server action queue (build use is server-originated only).
        for (int i = 0; i < 4; i++)
        {
            Assert.True(rig.Host.TryEnqueueServerAction(new PlayerActionMessage(
                new PlayerSessionId(ProtocolV2SharedSnapshotLoopbackIntegrationTests.PrimarySession),
                (ulong)(i + 1),
                new BuildUseIntent(ProtocolV2SharedSnapshotLoopbackIntegrationTests.BuildCells[i]))));
            for (int t = 0; t < 8; t++)
            {
                await primary.SendCommandAsync(walking: false, yaw: 0f);
                await rig.StepAsync();
            }
        }

        Vector3 built = ProtocolV2SharedSnapshotLoopbackIntegrationTests.BuildCells[0];
        GeometryCellKey builtKey = new((int)built.X, (int)built.Y, (int)built.Z);
        Assert.NotNull(serverMap.TryGetCubeAtCell(builtKey.X, builtKey.Y, builtKey.Z));

        // A fracture done on the server map behind the replication's back: the client only learns
        // of it through the checksum mismatch and the full-world resync it asks for.
        Assert.True(serverMap.FractureCubeIntoPieces(builtKey));
        for (int t = 0; t < 70; t++)
        {
            await primary.SendCommandAsync(walking: false, yaw: 0f);
            await rig.StepAsync();
        }

        await rig.VerifyAllAsync();
        if (outOfSync > 0)
        {
            await Task.Delay(ServerRoutingContext.WorldResyncCooldown);
            primary.Session.QueueWorldResyncRequest(WorldStateChecksum.Compute(world.Map));
            await rig.WaitUntilAsync(() => rig.Host.TryGetConnectionContext(primary.Connection.ConnectionId, out ServerRoutingContext? context) && context!.WorldResyncPending, "the resync request");
        }

        // A late joiner, while the headless map exports its world for the join.
        Wave12DeltaActorLoopbackTests.Peer late = await rig.JoinAsync("w12-headless-late");
        for (int t = 0; t < 70; t++)
        {
            await primary.SendCommandAsync(walking: false, yaw: 0f);
            await late.SendCommandAsync(walking: true, yaw: 1f);
            await rig.StepAsync();
        }

        await rig.VerifyAllAsync();
        output.WriteLine($"checksums {checksums}, out of sync {outOfSync}, resync worlds {resyncs}");
        Assert.True(resyncs >= 1, "The client never received the resync world.");
        Assert.Equal(WorldStateChecksum.Compute(serverMap), WorldStateChecksum.Compute(world.Map));
        Assert.NotNull(world.Map.TryGetRubbleAtCell(builtKey));

        // The client mirror renders what the headless server built and fractured.
        GeometryRenderExport export = world.Map.CapturePresentationGeometryRenderExport(new Vector3(5f, 1.5f, 5f), 40f);
        Assert.NotEmpty(export.Cells);
        Assert.Throws<InvalidOperationException>(() => serverMap.CapturePresentationGeometryRenderExport(new Vector3(5f, 1.5f, 5f), 40f));
        Assert.Throws<InvalidOperationException>(() => serverMap.CaptureFullGeometryRenderExport());
        Assert.Empty(serverMap.FindInvariantViolations());
        rig.AssertNoChainBreaksOrWithheldActors();
    }
}
