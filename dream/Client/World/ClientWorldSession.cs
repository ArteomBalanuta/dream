using System.Diagnostics;
using OpenTK.Mathematics;
using Dream.Application;
using Dream.Client.Input;
using Dream.Client.Presentation;
using Dream.Client.Replication;
using Dream.Multiplayer.Sessions;
using Dream.Server.Rules;
using Dream.Simulation.Combat;
using Dream.Simulation.Enemies;
using Dream.Simulation.Physics;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Rendering.Hud;

namespace Dream.Client.World;

// The desktop client's copy of the world. It owns the replicated map, the locally predicted
// player and the replicated enemies, and it only predicts the local player and steps physics.
//
// The client used to run a full WorldSessionRuntime in a "mirror" mode, which also executed
// server-only paths: syncing physics-moved cubes back into its map, queuing replication deltas,
// spawning a wolf, session registration, persistence restore and authoritative action code.
// Each new server feature ran on the client unless someone remembered to guard it, which caused
// the cube duplication bug. This type has none of those paths.
public sealed class ClientWorldSession : IClientWorldState, IDisposable
{
    private readonly bool _loadVisualAssets;
    private readonly CombatFormulaService _combatFormulaService;
    private readonly PhysicsSyncSystem _physicsSync = new();
    private double _lastPhysicsStepMilliseconds;

    public ClientWorldSession(
        int floorSize,
        Vector3 playerStartPosition,
        SceneEventHub events,
        bool loadVisualAssets = true,
        CombatFormulaService? combatFormulaService = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        _loadVisualAssets = loadVisualAssets;
        _combatFormulaService = combatFormulaService ?? CombatFormulaService.Default;
        Physics = new PhysicsIntegrator();
        Map = CreateMap(floorSize);
        var playerBody = new PhysicsFactory().CreatePlayerBody(playerStartPosition.ToBullet());
        Physics.RegisterPlayerBody(playerBody, PhysicsBodyOwnership.IntegratorOwned);
        Player = new Player(Map, Physics, playerBody, playerStartPosition, events, _combatFormulaService);
    }

    public Map Map { get; private set; }

    public PhysicsIntegrator Physics { get; }

    public Player Player { get; }

    public EnemyRegistry Enemies { get; } = new();

    public PlayerSessionId LocalPlayerSessionId => PlayerSessionId.LocalDefault;

    public PlayerInteractionMode InteractionMode { get; private set; } = PlayerInteractionMode.Building;

    public ulong CurrentServerTick { get; private set; }

    public ulong LastAppliedReplicatedWorldMutationSequence { get; private set; }

    public void SetAuthoritativeInteractionMode(PlayerInteractionMode interactionMode)
    {
        InteractionMode = interactionMode;
    }

    public void AcknowledgeReplicatedWorldMutations(ulong sequence)
    {
        LastAppliedReplicatedWorldMutationSequence = Math.Max(LastAppliedReplicatedWorldMutationSequence, sequence);
    }

    public bool TryResolveCombatActor(int actorId, out CombatActorHandle actor)
    {
        if (Player.Combatant.Actor.Id == actorId)
        {
            actor = Player.Combatant.Actor;
            return true;
        }

        return Enemies.TryGetActorById(actorId, out actor);
    }

    // One local prediction tick: the local player moves through physics and fracture pieces
    // settle locally. Placed cubes are kinematic replicated bodies and only move through
    // server deltas, so syncing dynamic bodies here touches rubble only.
    // Returns true on ticks where the held primary-use input should play the local use/attack
    // animation, at the authoritative attack cadence. The action itself is decided by the server.
    public bool AdvancePredictionTick(
        ulong tick,
        float deltaTime,
        PlayerCommandFrame commandFrame,
        PlayerSimulationTuning tuning,
        bool primaryUseHeld = false)
    {
        CurrentServerTick = tick;
        bool playPrimaryUse = Player.ConsumePrimaryUsePressed(
            new PlayerInteractionCommand(primaryUseHeld, false, false),
            Player.Combatant.DerivedStats.AttackIntervalSeconds,
            deltaTime);
        Player.Combatant.AdvanceCombat(deltaTime);
        if (commandFrame.RequestedMode.Mode.HasValue)
        {
            InteractionMode = commandFrame.RequestedMode.Mode.Value;
        }

        if (commandFrame.LookIntent.Facing.HasValue)
        {
            Player.SetFacing(commandFrame.LookIntent.Facing.Value);
        }

        Player.Update(
            deltaTime,
            new PlayerSimulationInput(
                PlayerController.CreateMovementCommand(Player.Camera, Player.IsNoclipEnabled, commandFrame.MoveIntent),
                new Vector2(commandFrame.LookIntent.YawDelta, commandFrame.LookIntent.PitchDelta),
                AllowGameplayInput: true,
                AllowLook: true),
            tuning);
        Stopwatch physicsStopwatch = Stopwatch.StartNew();
        Physics.StepSimulationFixed(deltaTime);
        physicsStopwatch.Stop();
        _lastPhysicsStepMilliseconds = physicsStopwatch.Elapsed.TotalMilliseconds;
        Player.SyncCameraFromBody(tuning);
        _physicsSync.SyncDynamicMapBodies(Map, Physics.DynamicMapCubeBodies);
        return playPrimaryUse;
    }

    public WorldSessionPresentationState CapturePresentationState(
        float maxRenderDistance,
        float presentationLeadSeconds = 0f,
        CameraViewMode cameraViewMode = CameraViewMode.FirstPerson)
    {
        Camera firstPersonCamera = Player.CreatePresentationCamera(presentationLeadSeconds);
        Camera presentationCamera = ThirdPersonCameraRig.Resolve(
            firstPersonCamera,
            Player.GetWorldPosition(),
            cameraViewMode);

        Stopwatch renderExportStopwatch = Stopwatch.StartNew();
        GeometryRenderExport visibleWorld = Map.CapturePresentationGeometryRenderExport(
            presentationCamera.Position,
            maxRenderDistance);
        renderExportStopwatch.Stop();

        return WorldSessionPresentationState.Create(
            presentationCamera,
            visibleWorld,
            Enemies.ActiveActors,
            Physics.MapCubeBodies,
            Player.PhysicsBody,
            Player.Combatant.Health,
            InteractionMode,
            Player.Combatant.DerivedStats,
            Player.Combatant.Stats,
            Player.Combatant.Level,
            Player.Combatant.Experience,
            _combatFormulaService.EvaluateExperienceToNextLevel(
                Player.Combatant.Level,
                Player.Combatant.Experience),
            new FramePerformanceSnapshot(
                renderExportStopwatch.Elapsed.TotalMilliseconds,
                _lastPhysicsStepMilliseconds,
                visibleWorld.CompatibilityCubes.Count + visibleWorld.DynamicCompatibilityCubes.Count,
                visibleWorld.Blocks.Count + visibleWorld.DynamicBlocks.Count,
                0,
                0,
                0,
                0,
                Physics.MapCubeBodies.Count));
    }

    public void RebuildMapForFloorSize(int floorSize)
    {
        Vector3 currentPlayerPosition = Player.GetWorldPosition();
        Map previousMap = Map;
        Physics.ResetMapBodies();
        Map rebuiltMap = CreateMap(floorSize);
        previousMap.CopyPlacedCubesTo(rebuiltMap);
        Map = rebuiltMap;
        Player.BindMap(rebuiltMap);
        Enemies.ClampToMapBounds(rebuiltMap);
        Player.SetWorldPosition(new Vector3(
            Math.Clamp(currentPlayerPosition.X, 0.5f, rebuiltMap.Width - 0.5f),
            Math.Clamp(currentPlayerPosition.Y, 1.5f, rebuiltMap.Height - 0.5f),
            Math.Clamp(currentPlayerPosition.Z, 0.5f, rebuiltMap.Depth - 0.5f)));
    }

    public void Dispose()
    {
        Physics.Dispose();
    }

    private Map CreateMap(int floorSize)
    {
        return new Map(
            Physics,
            loadVisualAssets: _loadVisualAssets,
            generateStartupTerrain: true,
            width: floorSize,
            depth: floorSize,
            replicatedCubeBodies: true);
    }
}
