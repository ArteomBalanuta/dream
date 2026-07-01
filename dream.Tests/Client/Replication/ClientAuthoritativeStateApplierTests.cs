using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Presentation;
using Dream.Client.Replication;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Rendering.Hud;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Tests.Client.Replication;


public class ClientAuthoritativeStateApplierTests
{
    [Fact]
    public void ClientCorrectionApplier_ApplyActorSnapshot_UpdatesReplicatedPlayerTarget()
    {
        FakeReplicatedPlayerTarget target = new();

        ActorSnapshot snapshot = new(
            1,
            CreatureKind.Player,
            "session-a",
            new Vector3(7f, 1.5f, 4f),
            Vector3.UnitX,
            65,
            120,
            true);

        ClientCorrectionApplier.ApplyActorSnapshot(target, snapshot);

        Assert.Equal(7f, target.Position.X, 3);
        Assert.Equal(1.5f, target.Position.Y, 3);
        Assert.Equal(4f, target.Position.Z, 3);
        Assert.Equal(65, target.CurrentHealth);
        Assert.Equal(120, target.MaxHealth);
        Assert.Equal(1f, target.Facing.X, 3);
    }

    [Fact]
    public void RemoteWorldMutationApplier_ApplyWorldSnapshot_ReconcilesPlacedCubes()
    {
        Map map = new(loadVisualAssets: false, width: 10, depth: 10);
        map.TryAddCube(new Vector3(8f, 1f, 8f), withPhysics: false);

        WorldSnapshot snapshot = new(
            10,
            8,
            10,
            [
                new Vector3(2f, 1f, 2f),
                new Vector3(3f, 1f, 3f)
            ]);

        RemoteWorldMutationApplier.ApplyWorldSnapshot(map, snapshot);

        Assert.NotNull(map.TryGetCubeAtCell(2, 1, 2));
        Assert.NotNull(map.TryGetCubeAtCell(3, 1, 3));
        Assert.Null(map.TryGetCubeAtCell(8, 1, 8));
    }

    [Fact]
    public void RemoteWorldMutationApplier_ApplyWorldMutations_AppliesWorldMutations()
    {
        Map map = new(loadVisualAssets: false, width: 10, depth: 10);
        map.TryAddCube(new Vector3(4f, 1f, 4f), withPhysics: false);

        WorldMutationDelta[] mutations =
        [
            new WorldMutationDelta(WorldMutationKind.CubeDestroyed, new Vector3(4f, 1f, 4f)),
            new WorldMutationDelta(WorldMutationKind.CubePlaced, new Vector3(6f, 1f, 6f))
        ];

        RemoteWorldMutationApplier.ApplyWorldMutations(map, mutations);

        Assert.Null(map.TryGetCubeAtCell(4, 1, 4));
        Assert.NotNull(map.TryGetCubeAtCell(6, 1, 6));
    }

    [Fact]
    public void RemoteWorldMutationApplier_ApplyWorldSnapshot_ReconcilesFracturedCubeCells()
    {
        Map map = new(loadVisualAssets: false, width: 10, depth: 10);
        map.TryAddCube(new Vector3(8f, 1f, 8f), withPhysics: false);

        WorldSnapshot snapshot = new(
            10,
            8,
            10,
            [new Vector3(2f, 1f, 2f)],
            [new Vector3(3f, 1f, 3f)]);

        RemoteWorldMutationApplier.ApplyWorldSnapshot(map, snapshot);

        GeometryCellRecord fracturedCell = Assert.IsType<GeometryCellRecord>(map.TryGetGeometryCellAtCell(3, 1, 3));
        GeometryRenderExport export = map.CaptureGeometryRenderExport(new Vector3(3.5f, 1.5f, 3.5f), 4f);

        Assert.NotNull(map.TryGetCubeAtCell(2, 1, 2));
        Assert.NotNull(fracturedCell.SubdivisionState);
        Assert.Contains(export.Blocks, block => block.CellKey == new GeometryCellKey(3, 1, 3));
        Assert.DoesNotContain(export.CompatibilityCubes, cube => cube.Position == new Vector3(3.5f, 1.5f, 3.5f));
        Assert.Null(map.TryGetCubeAtCell(8, 1, 8));
    }

    [Fact]
    public void RemoteWorldMutationApplier_ApplyWorldMutations_AppliesCubeFractureMutation()
    {
        Map map = new(loadVisualAssets: false, width: 10, depth: 10);
        map.TryAddCube(new Vector3(4f, 1f, 4f), withPhysics: false);

        WorldMutationDelta[] mutations =
        [
            new WorldMutationDelta(WorldMutationKind.CubeFractured, new Vector3(4f, 1f, 4f))
        ];

        RemoteWorldMutationApplier.ApplyWorldMutations(map, mutations);

        GeometryCellRecord fracturedCell = Assert.IsType<GeometryCellRecord>(map.TryGetGeometryCellAtCell(4, 1, 4));
        GeometryRenderExport export = map.CaptureGeometryRenderExport(new Vector3(4.5f, 1.5f, 4.5f), 4f);

        Assert.NotNull(fracturedCell.SubdivisionState);
        Assert.Contains(export.Blocks, block => block.CellKey == new GeometryCellKey(4, 1, 4));
        Assert.DoesNotContain(export.CompatibilityCubes, cube => cube.Position == new Vector3(4.5f, 1.5f, 4.5f));
    }

    [Fact]
    public void RemoteWorldMutationApplier_ApplySnapshotToRuntime_AddsCollisionBodiesForReplicatedPlacedCubes()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        int initialCollisionBodyCount = runtime.Physics.MapCubeBodies.Count;
        ServerSnapshotEnvelope snapshot = new(
            33,
            new Dictionary<string, ulong>(),
            Array.Empty<ActorSnapshot>(),
            PlayerInteractionMode.Building,
            new WorldSnapshot(
                runtime.Map.Width,
                runtime.Map.Height,
                runtime.Map.Depth,
                [new Vector3(6f, 1f, 6f)]),
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());

        RemoteWorldMutationApplier.ApplySnapshotToRuntime(runtime, snapshot);

        Assert.NotNull(runtime.Map.TryGetCubeAtCell(6, 1, 6));
        Assert.True(runtime.Physics.MapCubeBodies.Count > initialCollisionBodyCount);
    }

    [Fact]
    public void ClientJoinAcceptedApplier_ApplyToRuntime_RebuildsClientFloorFromInitialWorld()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
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
        using WorldSessionRuntime clientRuntime = CreateRuntime();
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

    [Fact]
    public void ClientJoinAcceptedApplier_ApplyToRuntime_PreservesAuthoritativeSubcellStartupShapes()
    {
        using WorldSessionRuntime clientRuntime = CreateRuntime();
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
            block => block.CellKey == new GeometryCellKey(18, 1, 16) &&
                block.SubcellKey.HasValue &&
                block.SideLength <= WorldMicroGrid.MicroCellSize + 0.0001f);
    }

    [Fact]
    public void ClientCorrectionApplier_ApplySnapshotMetadata_UpdatesAuthoritativeInteractionMode()
    {
        FakeReplicatedSessionStateTarget target = new();
        ServerSnapshotEnvelope snapshot = new(
            33,
            new Dictionary<string, ulong>(),
            Array.Empty<ActorSnapshot>(),
            PlayerInteractionMode.Combat,
            null,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());

        ClientCorrectionApplier.ApplySnapshotMetadata(target, snapshot);

        Assert.Equal(PlayerInteractionMode.Combat, target.InteractionMode);
    }

    [Fact]
    public void ClientAuthoritativeActionApplier_ApplyActionResult_AppliesAcceptedWorldMutationToRuntime()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        AuthoritativeActionResult result = new(
            "session-a",
            17,
            41,
            Accepted: true,
            Payload: new WorldMutationResult(
                WorldMutationKind.CubePlaced,
                new Vector3(6f, 1f, 6f),
                Applied: true));

        ClientAuthoritativeActionApplier.ApplyActionResult(runtime, new SceneEventHub(), result);

        Assert.NotNull(runtime.Map.TryGetCubeAtCell(6, 1, 6));
    }

    [Fact]
    public void ClientAuthoritativeActionApplier_ApplyActionResult_PublishesDamageEventForAcceptedCombatResult()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        SceneEventHub events = new();
        List<DamageAppliedEvent> observed = [];
        using IDisposable subscription = events.Subscribe<DamageAppliedEvent>(evt => observed.Add(evt));
        AuthoritativeActionResult result = new(
            "session-a",
            19,
            44,
            Accepted: true,
            Payload: new CombatResolutionResult(
                runtime.Player.Combatant.Actor.Id,
                CreatureKind.Player,
                DamageApplied: 9,
                TargetDied: false,
                CurrentHealth: 91,
                MaxHealth: 100));

        ClientAuthoritativeActionApplier.ApplyActionResult(runtime, events, result);

        DamageAppliedEvent damage = Assert.Single(observed);
        Assert.Equal(9, damage.Amount);
        Assert.Equal(runtime.Player.Combatant.Actor.Id, damage.Actor.Id);
    }

    [Fact]
    public void ClientAuthoritativeActionApplier_ApplyActionResult_AppliesPlayerStatsUpdateToRuntime()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        AuthoritativeActionResult result = new(
            "session-a",
            20,
            45,
            Accepted: true,
            Payload: new PlayerStatsUpdateResult(new PlayerCombatStatsSnapshot(18, 3.5f, 14)));

        ClientAuthoritativeActionApplier.ApplyActionResult(runtime, new SceneEventHub(), result);

        Assert.Equal(18, runtime.Player.Combatant.Stats.Strength);
        Assert.Equal(3.5f, runtime.Player.Combatant.Stats.AttackSpeed, 3);
        Assert.Equal(14, runtime.Player.Combatant.Stats.Vitality);
    }

    [Fact]
    public void ClientSnapshotDamageApplier_ApplyToRuntime_PublishesDamageEventForResolvedEnemy()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        SceneEventHub events = new();
        List<DamageAppliedEvent> observed = [];
        using IDisposable subscription = events.Subscribe<DamageAppliedEvent>(evt => observed.Add(evt));

        RemoteEnemyStateApplier.ApplyActorSnapshots(runtime.Enemies,
        [
            new ActorSnapshot(
                3001,
                CreatureKind.Enemy,
                null,
                new Vector3(6f, 1.5f, 6f),
                -Vector3.UnitZ,
                70,
                100,
                true)
        ]);

        ServerSnapshotEnvelope snapshot = new(
            77,
            new Dictionary<string, ulong>(),
            Array.Empty<ActorSnapshot>(),
            PlayerInteractionMode.Combat,
            null,
            Array.Empty<WorldMutationDelta>(),
            [new DamageEventDelta(3001, CreatureKind.Enemy, 11, ActorDied: false, CurrentHealth: 59, MaxHealth: 100)]);

        ClientSnapshotDamageApplier.ApplyToRuntime(runtime, events, snapshot);

        DamageAppliedEvent damage = Assert.Single(observed);
        Assert.Equal(11, damage.Amount);
        Assert.Equal(3001, damage.Actor.Id);
    }

    [Fact]
    public void ClientAuthoritativeActionApplier_ApplyActionResult_AppliesRejectedCorrectionImmediately()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        runtime.Player.SetFacing(-Vector3.UnitZ);
        AuthoritativeActionResult result = new(
            "session-a",
            23,
            50,
            Accepted: false,
            Payload: null,
            Correction: new ClientCorrectionDelta(
                new Vector3(8f, 1.5f, 7f),
                Vector3.UnitX),
            Rejection: new RejectedUseDelta("Rejected"));

        ClientAuthoritativeActionApplier.ApplyActionResult(runtime, new SceneEventHub(), result);

        Vector3 position = runtime.Player.GetWorldPosition();
        Assert.Equal(8f, position.X, 3);
        Assert.Equal(1.5f, position.Y, 3);
        Assert.Equal(7f, position.Z, 3);
        Assert.Equal(0f, runtime.Player.Camera.Front.X, 3);
        Assert.Equal(-1f, runtime.Player.Camera.Front.Z, 3);
    }

    [Fact]
    public void ClientCorrectionApplier_ApplySnapshotToRuntime_DoesNotOverwriteLocalCameraFacing()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        runtime.Player.SetFacing(Vector3.UnitX);
        ServerSnapshotEnvelope snapshot = new(
            33,
            new Dictionary<string, ulong>(),
            [
                new ActorSnapshot(
                    runtime.Player.Combatant.Actor.Id,
                    CreatureKind.Player,
                    runtime.LocalPlayerSessionId.Value,
                    new Vector3(7f, 1.5f, 4f),
                    -Vector3.UnitZ,
                    65,
                    120,
                    true)
            ],
            PlayerInteractionMode.Combat,
            null,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());

        ClientCorrectionApplier.ApplySnapshotToRuntime(
            runtime,
            snapshot,
            runtime.LocalPlayerSessionId.Value);

        Assert.Equal(1f, runtime.Player.Camera.Front.X, 3);
        Assert.Equal(0f, runtime.Player.Camera.Front.Z, 3);
        Assert.Equal(7f, runtime.Player.GetWorldPosition().X, 3);
    }

    [Fact]
    public void ClientCorrectionApplier_ApplySnapshotToRuntime_AppliesAuthoritativeCombatStats()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        ServerSnapshotEnvelope snapshot = new(
            34,
            new Dictionary<string, ulong>(),
            [
                new ActorSnapshot(
                    runtime.Player.Combatant.Actor.Id,
                    CreatureKind.Player,
                    runtime.LocalPlayerSessionId.Value,
                    new Vector3(5f, 1.5f, 5f),
                    -Vector3.UnitZ,
                    110,
                    110,
                    true,
                    PlayerInteractionMode.Combat,
                    new PlayerCombatStatsSnapshot(20, 4f, 12))
            ],
            PlayerInteractionMode.Combat,
            null,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());

        ClientCorrectionApplier.ApplySnapshotToRuntime(
            runtime,
            snapshot,
            runtime.LocalPlayerSessionId.Value);

        Assert.Equal(20, runtime.Player.Combatant.Stats.Strength);
        Assert.Equal(4f, runtime.Player.Combatant.Stats.AttackSpeed, 3);
        Assert.Equal(12, runtime.Player.Combatant.Stats.Vitality);
    }

    [Fact]
    public void ClientAuthoritativeActionApplier_ApplyActionResult_PublishesRejectedActionEvent()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        SceneEventHub events = new();
        List<AuthoritativeActionRejectedEvent> observed = [];
        using IDisposable subscription = events.Subscribe<AuthoritativeActionRejectedEvent>(evt => observed.Add(evt));
        AuthoritativeActionResult result = new(
            "session-a",
            24,
            51,
            Accepted: false,
            Payload: null,
            Rejection: new RejectedUseDelta("Build rejected", "occupied"));

        ClientAuthoritativeActionApplier.ApplyActionResult(runtime, events, result);

        AuthoritativeActionRejectedEvent rejection = Assert.Single(observed);
        Assert.Equal("session-a", rejection.Result.SessionId);
        Assert.Equal((ulong)24, rejection.Result.ActionSequence);
        Assert.Equal("Build rejected", rejection.Result.Rejection?.Reason);
    }

    [Fact]
    public void ClientAuthoritativeActionApplier_ApplyActionResult_RecordsDiagnosticsForRejectedAction()
    {
        using WorldSessionRuntime runtime = CreateRuntime();
        RecordingDiagnosticsSink diagnostics = new();
        AuthoritativeActionResult result = new(
            "session-a",
            25,
            52,
            Accepted: false,
            Payload: null,
            Correction: new ClientCorrectionDelta(new Vector3(9f, 1.5f, 8f), Vector3.UnitY),
            Rejection: new RejectedUseDelta("Out of range"));

        ClientAuthoritativeActionApplier.ApplyActionResult(runtime, new SceneEventHub(), result, diagnostics);

        Assert.Single(diagnostics.RecordedResults);
        Assert.True(diagnostics.CorrectionApplied);
        Assert.Equal("session-a", diagnostics.RecordedResults[0].SessionId);
        Assert.Equal((ulong)25, diagnostics.RecordedResults[0].ActionSequence);
    }

    private static WorldSessionRuntime CreateRuntime()
    {
        return new WorldSessionRuntime(
            floorSize: 10,
            playerStartPosition: new Vector3(5f, 1.5f, 5f),
            new SceneEventHub(),
            loadVisualAssets: false);
    }

    private sealed class FakeReplicatedPlayerTarget : IReplicatedPlayerTarget
    {
        public Vector3 Position { get; private set; }

        public Vector3 Facing { get; private set; }

        public int CurrentHealth { get; private set; }

        public int MaxHealth { get; private set; }

        public void SetWorldPosition(Vector3 position) => Position = position;

        public void SetFacing(Vector3 facing) => Facing = facing;

        public void SetHealth(int currentHealth, int maxHealth)
        {
            CurrentHealth = currentHealth;
            MaxHealth = maxHealth;
        }
    }

    private sealed class FakeReplicatedSessionStateTarget : IReplicatedSessionStateTarget
    {
        public PlayerInteractionMode InteractionMode { get; private set; } = PlayerInteractionMode.Building;

        public void SetAuthoritativeInteractionMode(PlayerInteractionMode interactionMode)
        {
            InteractionMode = interactionMode;
        }
    }

    private sealed class RecordingDiagnosticsSink : IMultiplayerDiagnosticsSink
    {
        public List<AuthoritativeActionResult> RecordedResults { get; } = [];

        public bool CorrectionApplied { get; private set; }

        public void RecordSnapshotApplied(ServerSnapshotEnvelope snapshot, string sessionId)
        {
        }

        public void RecordActionResult(AuthoritativeActionResult result, bool correctionApplied)
        {
            RecordedResults.Add(result);
            CorrectionApplied = correctionApplied;
        }
    }
}
