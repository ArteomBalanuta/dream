using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server;
using Dream.Simulation.Determinism;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Simulation.Runtime;

// Wave T2c: the building menu. A client picks a build material with the "build.material" action;
// the server keeps it per session and the authoritative placement uses it, else the supporting
// cell's material (Auto, which never copies a lamp).
public class WaveT2BuildMaterialTests
{
    private const string SessionA = "session-a";
    private const string SessionB = "session-b";
    private static readonly Vector3 LampCell = new(7f, 1f, 7f);
    private static readonly Vector3 AboveLampCell = new(7f, 2f, 7f);

    [Fact]
    public void Catalog_PlaceableIsStoneDirtLamp_AndAutoNeverYieldsLamp()
    {
        Assert.Equal(
            [GeometryMaterialId.Stone, GeometryMaterialId.Dirt, GeometryMaterialId.Lamp],
            BuildMaterialCatalog.Placeable);
        foreach (GeometryMaterialId material in Enum.GetValues<GeometryMaterialId>())
        {
            Assert.Equal(BuildMaterialCatalog.Placeable.Contains(material), BuildMaterialCatalog.IsPlaceable(material));
            Assert.NotEqual(GeometryMaterialId.Lamp, BuildMaterialCatalog.ResolveAuto(material));
            Assert.Equal(
                BuildMaterialCatalog.IsPlaceable(material) ? material : BuildMaterialCatalog.ResolveAuto(GeometryMaterialId.Sand),
                BuildMaterialCatalog.ResolvePlacement(material, GeometryMaterialId.Sand));
        }

        Assert.Equal(GeometryMaterialId.Stone, BuildMaterialCatalog.ResolveAuto(GeometryMaterialId.Lamp));
        Assert.Equal(GeometryMaterialId.Grass, BuildMaterialCatalog.ResolveAuto(null));
        Assert.Equal(GeometryMaterialId.Sand, BuildMaterialCatalog.ResolveAuto(GeometryMaterialId.Sand));
        Assert.True(BuildMaterialCatalog.IsValidSelection(null));
        Assert.False(BuildMaterialCatalog.IsValidSelection((GeometryMaterialId)99));
    }

    [Fact]
    public void SelectingLamp_PlacesALamp_AndReplicatesIt()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession(SessionA, "Player A");
        host.ExportSnapshot();

        AuthoritativeActionResult select = host.ProcessAction(Select(SessionA, 1, GeometryMaterialId.Lamp));
        AuthoritativeActionResult build = host.ProcessAction(Build(SessionA, 2, LampCell));

        Assert.True(select.Accepted);
        Assert.Null(select.Payload);
        Assert.True(build.Accepted, build.Rejection?.Reason);
        Assert.Equal(GeometryMaterialId.Lamp, runtime.ResolveSelectedBuildMaterial(new PlayerSessionId(SessionA)));
        Cube cube = Assert.IsType<Cube>(runtime.Map.TryGetCubeAtCell(7, 1, 7));
        Assert.Equal(GeometryMaterialId.Lamp, cube.MaterialId);
        Assert.Equal(GeometryMaterialId.Lamp, runtime.Map.TryGetGeometryCellAtCell(7, 1, 7)!.Definition.MaterialId);
        WorldMutationDelta placed = Assert.Single(host.ExportSnapshot().WorldMutations, mutation => mutation.Kind == WorldMutationKind.CubePlaced);
        Assert.Equal(GeometryMaterialId.Lamp, placed.MaterialId);
    }

    [Fact]
    public void AutoOnTopOfALamp_PlacesStone_AndSelectionsApplyInActionOrder()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession(SessionA, "Player A");

        Assert.True(host.ProcessAction(Select(SessionA, 1, GeometryMaterialId.Lamp)).Accepted);
        Assert.True(host.ProcessAction(Build(SessionA, 2, LampCell)).Accepted);
        Assert.True(host.ProcessAction(Select(SessionA, 3, materialId: null)).Accepted);
        AuthoritativeActionResult autoBuild = host.ProcessAction(Build(SessionA, 4, AboveLampCell));

        Assert.True(autoBuild.Accepted, autoBuild.Rejection?.Reason);
        Assert.Null(runtime.ResolveSelectedBuildMaterial(new PlayerSessionId(SessionA)));
        Assert.Equal(0, runtime.BuildMaterialSelectionCount);
        Assert.Equal(GeometryMaterialId.Lamp, runtime.Map.TryGetCubeAtCell(7, 1, 7)!.MaterialId);
        Assert.Equal(GeometryMaterialId.Stone, runtime.Map.TryGetCubeAtCell(7, 2, 7)!.MaterialId);
    }

    [Fact]
    public void Auto_KeepsTheSupportCellsMaterial_AsBeforeTheMenu()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession(SessionA, "Player A");
        GeometryMaterialId? support = runtime.Map.TryGetGeometryCellAtCell(7, 0, 7)?.Definition.MaterialId;

        Assert.True(host.ProcessAction(Build(SessionA, 1, LampCell)).Accepted);

        Assert.Equal(BuildMaterialCatalog.ResolveAuto(support), runtime.Map.TryGetCubeAtCell(7, 1, 7)!.MaterialId);
    }

    [Theory]
    [InlineData(GeometryMaterialId.Sand)]
    [InlineData(GeometryMaterialId.Grass)]
    [InlineData((GeometryMaterialId)99)]
    [InlineData((GeometryMaterialId)(-1))]
    public void InvalidMaterial_IsRejected_AndKeepsThePreviousSelection(GeometryMaterialId invalid)
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession(SessionA, "Player A");
        Assert.True(host.ProcessAction(Select(SessionA, 1, GeometryMaterialId.Dirt)).Accepted);

        AuthoritativeActionResult rejected = host.ProcessAction(Select(SessionA, 2, invalid));

        Assert.False(rejected.Accepted);
        Assert.Contains("not placeable", rejected.Rejection!.Reason, StringComparison.Ordinal);
        Assert.Equal(GeometryMaterialId.Dirt, runtime.ResolveSelectedBuildMaterial(new PlayerSessionId(SessionA)));
        Assert.True(host.ProcessAction(Build(SessionA, 3, LampCell)).Accepted);
        Assert.Equal(GeometryMaterialId.Dirt, runtime.Map.TryGetCubeAtCell(7, 1, 7)!.MaterialId);
    }

    [Fact]
    public void Selection_IsPerSession_AndNotInTheChecksum()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession(SessionA, "Player A");
        host.JoinSession(SessionB, "Player B");
        ulong before = SimulationStateChecksum.Compute(runtime);

        Assert.True(host.ProcessAction(Select(SessionA, 1, GeometryMaterialId.Lamp)).Accepted);

        Assert.Equal(before, SimulationStateChecksum.Compute(runtime));
        Assert.Equal(GeometryMaterialId.Lamp, runtime.ResolveSelectedBuildMaterial(new PlayerSessionId(SessionA)));
        Assert.Null(runtime.ResolveSelectedBuildMaterial(new PlayerSessionId(SessionB)));
        Assert.Null(runtime.ResolveSelectedBuildMaterial(new PlayerSessionId("unknown")));
        Assert.Equal(1, runtime.BuildMaterialSelectionCount);
    }

    // A reconnect under the same session id reuses its ConnectedPlayer, so the selection used to
    // survive while the reconnected client's HUD showed Auto. The leave now drops it.
    [Fact]
    public void Selection_IsDroppedOnLeave_SoAReconnectStartsFromAuto()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession(SessionA, "Player A");
        Assert.True(host.ProcessAction(Select(SessionA, 1, GeometryMaterialId.Lamp)).Accepted);
        Assert.Equal(1, runtime.BuildMaterialSelectionCount);

        host.ReleaseSession(SessionA);

        Assert.Equal(0, runtime.BuildMaterialSelectionCount);
        host.JoinSession(SessionA, "Player A");
        Assert.Null(runtime.ResolveSelectedBuildMaterial(new PlayerSessionId(SessionA)));
        Assert.True(host.ProcessAction(Build(SessionA, 1, LampCell)).Accepted);
        Assert.NotEqual(GeometryMaterialId.Lamp, runtime.Map.TryGetCubeAtCell(7, 1, 7)!.MaterialId);
    }

    [Fact]
    public void StaleSelectionSequence_IsRejected()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        using RuntimeAuthoritativeSessionHost host = new(runtime);
        host.JoinSession(SessionA, "Player A");
        Assert.True(host.ProcessAction(Select(SessionA, 5, GeometryMaterialId.Stone)).Accepted);

        Assert.False(host.ProcessAction(Select(SessionA, 5, GeometryMaterialId.Lamp)).Accepted);
        Assert.Equal(GeometryMaterialId.Stone, runtime.ResolveSelectedBuildMaterial(new PlayerSessionId(SessionA)));
    }

    [Theory]
    [InlineData(GeometryMaterialId.Lamp)]
    [InlineData(GeometryMaterialId.Stone)]
    [InlineData(null)]
    public void SelectionMessage_RoundTripsThroughTheCodec(GeometryMaterialId? materialId)
    {
        TransportJsonCodec codec = new();
        PlayerActionMessage message = Select(SessionA, 42, materialId);

        byte[] encoded = codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, 7), message);
        DecodedProtocolMessage decoded = codec.Decode(encoded);

        string json = System.Text.Encoding.UTF8.GetString(encoded);
        Assert.Contains("\"build.material\"", json, StringComparison.Ordinal);
        PlayerActionMessage roundTripped = Assert.IsType<PlayerActionMessage>(decoded.Message);
        Assert.Equal(42UL, roundTripped.ActionSequence);
        Assert.Equal(materialId, Assert.IsType<BuildMaterialSelectIntent>(roundTripped.Payload).MaterialId);
    }

    [Fact]
    public void SelectionOverTheWire_IsQueued_AndAppliedByTheServer()
    {
        using DedicatedServerHost host = DedicatedServerHost.CreateRuntimeAttached(persistence: null);
        Assert.IsType<JoinAcceptedMessage>(Assert.Single(
            host.HandleClientMessage("connection-a", new ClientJoinRequestMessage(SessionA, "A"))));

        IReadOnlyList<IServerToClientMessage> replies = host.HandleClientMessage("connection-a", Select(SessionA, 1, GeometryMaterialId.Lamp));

        Assert.DoesNotContain(replies, reply => reply is ServerActionMessage { Result.Accepted: false });
        host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
        Assert.Equal(GeometryMaterialId.Lamp, host.Runtime!.ResolveSelectedBuildMaterial(new PlayerSessionId(SessionA)));
    }

    // No repositories: persistence of a lamp is covered by WaveT2LampPersistenceTests on SQLite.
    private static WorldSessionRuntime CreateRuntime()
    {
        WorldSessionRuntime runtime = new(10, new Vector3(5f, 1.5f, 5f), new SceneEventHub());
        runtime.Enemies.Clear();
        return runtime;
    }

    private static PlayerActionMessage Select(string session, ulong sequence, GeometryMaterialId? materialId)
    {
        return new PlayerActionMessage(new PlayerSessionId(session), sequence, new BuildMaterialSelectIntent(materialId));
    }

    private static PlayerActionMessage Build(string session, ulong sequence, Vector3 cell)
    {
        return new PlayerActionMessage(new PlayerSessionId(session), sequence, new BuildUseIntent(cell));
    }
}
