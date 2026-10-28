using System.Diagnostics;
using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Presentation;
using Dream.Client.Replication;
using Dream.Client.World;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Rendering.Hud;
using Dream.Simulation.Combat;
using Dream.Simulation.Physics;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using static Dream.Tests.Client.Replication.ClientAuthoritativeStateApplierTests;

namespace Dream.Tests.Client.Replication;

// ClientAuthoritativeStateApplierTests' join-accepted cases that rebuild the client from an
// authoritative startup world. Each generates full startup terrain, so they run as their own
// class, in parallel with the rest; the helpers are the main class's.
public class ClientJoinAcceptedStartupTerrainApplierTests
{
    [Fact]
    public void ClientJoinAcceptedApplier_ApplyToRuntime_RebuildsClientFloorFromInitialWorld()
    {
        using ClientWorldSession runtime = CreateRuntime();
        ClientScenePresenter presenter = new(localSessionId: "session-a");
        JoinAcceptedMessage joinAccepted = new(
            "session-a",
            101,
            0,
            new WorldSnapshot(
                10,
                runtime.Map.Height,
                10,
                Array.Empty<Vector3>()),
            [
                new PlayerRosterEntry("session-a", 101, "Player A")
            ]);

        ClientJoinAcceptedApplier.ApplyToRuntime(runtime, joinAccepted);
        ClientJoinAcceptedApplier.ApplyToPresenter(presenter, joinAccepted);

        Assert.Equal(10, runtime.Map.Width);
        Assert.Equal(10, runtime.Map.Depth);
        Assert.True(runtime.Physics.MapCubeBodies.Count >= 1);
        PlayerRosterEntry rosterEntry = Assert.Single(presenter.BuildPlayerRoster());
        Assert.Equal("Player A", rosterEntry.DisplayName);
    }

    [Fact]
    public void ClientJoinAcceptedApplier_ApplyToRuntime_PreservesAuthoritativeStartupTerrain()
    {
        using ClientWorldSession clientRuntime = CreateRuntime();
        using RuntimeAuthoritativeSessionHost serverHost = RuntimeAuthoritativeSessionHost.CreateDefault();
        JoinAcceptedMessage joinAccepted = serverHost.JoinSession("session-a", "Player A");

        ClientJoinAcceptedApplier.ApplyToRuntime(clientRuntime, joinAccepted);

        GeometryCellRecord bridgeCell = Assert.IsType<GeometryCellRecord>(
            clientRuntime.Map.TryGetGeometryCellAtCell(7, 1, 5));
        GeometryCellRecord islandSupportCell = Assert.IsType<GeometryCellRecord>(
            clientRuntime.Map.TryGetGeometryCellAtCell(12, 1, 8));
        GeometryRenderExport renderExport = clientRuntime.Map.CaptureGeometryRenderExport(
            new Vector3(8f, 1f, 6f),
            radius: 8f);

        Assert.Equal(WorldDefaults.StartupFloorSize, clientRuntime.Map.Width);
        Assert.Equal(WorldDefaults.StartupFloorSize, clientRuntime.Map.Depth);
        Assert.Equal(GeometryMaterialId.Dirt, bridgeCell.Definition.MaterialId);
        Assert.Equal(GeometryMaterialId.Stone, islandSupportCell.Definition.MaterialId);
        Assert.Contains(
            renderExport.CompatibilityCubes,
            cube => cube.Position == new Vector3(7.5f, 1.5f, 5.5f));
    }
}
