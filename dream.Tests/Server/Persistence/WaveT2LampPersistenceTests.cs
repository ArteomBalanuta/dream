using OpenTK.Mathematics;
using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Server.Persistence;
using Dream.Simulation.Combat;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Server.Persistence;

// Wave T2c: a lamp goes through the existing placed-cube persistence unchanged. material_id is
// stored as text and parsed back with Enum.Parse, so "Lamp" survives a SQLite restart, and the
// fracture pieces of a broken lamp keep the Lamp material.
public sealed class WaveT2LampPersistenceTests
{
    private const string PlayerName = "Player A";
    private const int FloorSize = 10;
    private static readonly PlayerSessionId SessionId = new("session-a");
    private static readonly Vector3 LampCell = new(7f, 1f, 7f);

    [Fact]
    public void PlacedLamp_SurvivesACheckpointAndASqliteRestart()
    {
        using IncrementalCheckpointTests.TempDatabase database = new();
        new SqlitePlayerProfileRepository(database.Schema).GetOrCreate(PlayerName);
        using (WorldSessionRuntime runtime = CreateRuntime(database))
        using (RuntimeAuthoritativeSessionHost host = new(runtime))
        {
            host.JoinSession(SessionId.Value, PlayerName);
            runtime.Enemies.Clear();
            PlaceLamp(host);
            Assert.Equal(GeometryMaterialId.Lamp, runtime.Map.TryGetCubeAtCell(7, 1, 7)!.MaterialId);

            runtime.PersistCheckpoint();
        }

        PersistedPlacedCube row = Assert.Single(new SqliteWorldPersistenceRepository(database.Schema).LoadPlacedCubes());
        Assert.Equal(GeometryMaterialId.Lamp, row.MaterialId);
        Assert.Equal(LampCell, row.CellPosition);
        Assert.Equal(PlayerName, row.OwnerPlayerName);

        using (WorldSessionRuntime restored = CreateRuntime(database))
        {
            restored.WriteCheckpointOnDispose = false;
            Cube cube = Assert.Single(restored.Map.SolidCubes, candidate =>
                (candidate.Position - (LampCell + new Vector3(0.5f))).Length < 0.6f);
            Assert.Equal(GeometryMaterialId.Lamp, cube.MaterialId);
            Assert.Equal(GeometryMaterialId.Lamp, restored.Map.TryGetGeometryCellAtCell(7, 1, 7)!.Definition.MaterialId);
        }

        Assert.Equal("ok", database.IntegrityCheck());
    }

    [Fact]
    public void BrokenLamp_FracturePieces_KeepTheLampMaterial_AcrossARestart()
    {
        using IncrementalCheckpointTests.TempDatabase database = new();
        new SqlitePlayerProfileRepository(database.Schema).GetOrCreate(PlayerName);
        int lampId;
        using (WorldSessionRuntime runtime = CreateRuntime(database))
        using (RuntimeAuthoritativeSessionHost host = new(runtime))
        {
            host.JoinSession(SessionId.Value, PlayerName);
            runtime.Enemies.Clear();
            PlaceLamp(host);
            lampId = (int)Assert.Single(runtime.WorldPersistence!.LoadPlacedCubes()).ObjectId.Value;
            Assert.True(host.ProcessAction(new PlayerActionMessage(
                SessionId,
                3,
                new PlayerStatsUpdateIntent(new PlayerCombatStatsSnapshot(Strength: PlayerCombatStats.MaxStrength, AttackSpeed: 2f, Vitality: 10)))).Accepted);
            runtime.SetAuthoritativeInteractionMode(PlayerInteractionMode.Combat);
            runtime.Player.SetWorldPosition(new Vector3(5f, 1.5f, 7.5f));
            runtime.Player.SetFacing(Vector3.UnitX);

            AuthoritativeActionResult hit = host.ProcessAction(new PlayerActionMessage(
                SessionId,
                4,
                new CombatUseIntent(new Vector3(5f, 1.5f, 7.5f), Vector3.UnitX)));

            Assert.True(hit.Accepted, hit.Rejection?.Reason);
            Assert.True(Assert.IsType<CombatResolutionResult>(hit.Payload).TargetDied);
            runtime.PersistCheckpoint();
        }

        SqliteWorldPersistenceRepository repository = new(database.Schema);
        Assert.Empty(repository.LoadPlacedCubes());
        PersistedCubeFracturePiece[] pieces = repository.LoadCubeFracturePieces().ToArray();
        Assert.NotEmpty(pieces);
        Assert.All(pieces, piece =>
        {
            Assert.Equal(lampId, (int)piece.OriginObjectId.Value);
            Assert.Equal(GeometryMaterialId.Lamp, piece.MaterialId);
        });

        using WorldSessionRuntime restored = CreateRuntime(database, headlessMap: false);
        restored.WriteCheckpointOnDispose = false;
        GeometryRenderExport export = restored.Map.CaptureFullGeometryRenderExport();
        Assert.Contains(export.DynamicBlocks, block => block.MaterialId == GeometryMaterialId.Lamp);
    }

    private static void PlaceLamp(RuntimeAuthoritativeSessionHost host)
    {
        AuthoritativeActionResult select = host.ProcessAction(new PlayerActionMessage(
            SessionId,
            1,
            new BuildMaterialSelectIntent(GeometryMaterialId.Lamp)));
        AuthoritativeActionResult build = host.ProcessAction(new PlayerActionMessage(
            SessionId,
            2,
            new BuildUseIntent(LampCell)));
        Assert.True(select.Accepted, select.Rejection?.Reason);
        Assert.True(build.Accepted, build.Rejection?.Reason);
    }

    private static WorldSessionRuntime CreateRuntime(IncrementalCheckpointTests.TempDatabase database, bool headlessMap = true)
    {
        return new WorldSessionRuntime(
            FloorSize,
            new Vector3(5f, 1.5f, 5f),
            new SceneEventHub(),
            playerProfiles: new SqlitePlayerProfileRepository(database.Schema),
            worldPersistence: new SqliteWorldPersistenceRepository(database.Schema),
            checkpointWriter: new SqlitePersistenceCheckpointWriter(database.Schema),
            headlessMap: headlessMap);
    }
}
