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
using Dream.Tests.Utilities;
using static Dream.Tests.Client.Replication.ClientAuthoritativeStateApplierTests;

namespace Dream.Tests.Client.Replication;

// ClientAuthoritativeStateApplierTests' join-accepted cases that restore authoritative subcell
// shapes. Each generates full startup terrain, so they run as their own class, in parallel with
// the rest; the helpers are the main class's.
public class ClientJoinAcceptedSubcellApplierTests
{
    [Fact]
    public void ClientJoinAcceptedApplier_ApplyToRuntime_PreservesAuthoritativeSubcellStartupShapes()
    {
        using ClientWorldSession clientRuntime = CreateRuntime();
        using RuntimeAuthoritativeSessionHost serverHost = RuntimeAuthoritativeSessionHost.CreateDefault();
        JoinAcceptedMessage joinAccepted = serverHost.JoinSession("session-a", "Player A");

        ClientJoinAcceptedApplier.ApplyToRuntime(clientRuntime, joinAccepted);

        GeometryCellRecord pyramidCell = Assert.IsType<GeometryCellRecord>(
            clientRuntime.Map.TryGetGeometryCellAtCell(18, 1, 16));
        GeometryRenderExport renderExport = clientRuntime.Map.CaptureGeometryRenderExport(
            new Vector3(18.5f, 1.5f, 16.5f),
            radius: 4f);

        Assert.NotNull(pyramidCell.SubdivisionState);
        Assert.Null(pyramidCell.FracturePieces);
        Assert.DoesNotContain(
            renderExport.CompatibilityCubes,
            cube => cube.Position == new Vector3(18.5f, 1.5f, 16.5f));
        Assert.Contains(
            renderExport.Blocks,
            block => IntersectsCell(block, new GeometryCellKey(18, 1, 16)) &&
                block.SideLength <= WorldMicroGrid.MicroCellSize + 0.0001f);
    }

    [Fact]
    public void ClientJoinAcceptedApplier_ApplyToRuntime_BatchesAuthoritativeSubcellRestore()
    {
        using ClientWorldSession clientRuntime = CreateRuntime();
        using RuntimeAuthoritativeSessionHost serverHost = RuntimeAuthoritativeSessionHost.CreateDefault();
        JoinAcceptedMessage joinAccepted = serverHost.JoinSession("session-a", "Player A");

        // The apply is synchronous and single-threaded, so its CPU time is the work itself. Wall time
        // under the parallel suite also counted waiting for a core: it read 15.6-18.7 s on loaded
        // runs while the work was about 1 s (ARCH-1b). Same 15 s budget, on the work.
        TimeSpan cpu = ThreadCpuTime.Measure(
            () => ClientJoinAcceptedApplier.ApplyToRuntime(clientRuntime, joinAccepted),
            out TimeSpan wall);

        Assert.True(
            cpu < TimeSpan.FromSeconds(15),
            $"Expected join world application to avoid per-cell global rebuilds, but it took {cpu} of CPU ({wall} wall).");
    }
}
