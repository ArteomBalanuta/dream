using System.Diagnostics;
using OpenTK.Mathematics;
using Dream.Application;
using Dream.Client.Input;
using Dream.Client.Presentation;
using Dream.Client.Replication;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Rules;
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
    private readonly CombatFormulaService _combatFormulaService;
    private readonly PhysicsSyncSystem _physicsSync = new();
    private double _lastPhysicsStepMilliseconds;
    // Reused first-person camera for CapturePresentationState. It never leaves this type:
    // ThirdPersonCameraRig.Resolve copies it into the camera the presentation state carries.
    private readonly Camera _firstPersonCameraScratch = new(Vector3.Zero);
    private bool _disposed;

    public ClientWorldSession(
        int floorSize,
        Vector3 playerStartPosition,
        SceneEventHub events,
        CombatFormulaService? combatFormulaService = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        _combatFormulaService = combatFormulaService ?? CombatFormulaService.Default;
        Physics = new PhysicsIntegrator();
        World = WorldDescriptor.ForFloorSize(floorSize);
        Map = CreateMap(floorSize);
        var playerBody = new PhysicsFactory().CreatePlayerBody(playerStartPosition.ToBullet());
        Physics.RegisterPlayerBody(playerBody, PhysicsBodyOwnership.IntegratorOwned);
        Player = new Player(Map, Physics, playerBody, playerStartPosition, events, _combatFormulaService);
    }

    public Map Map { get; private set; }

    public WorldDescriptor World { get; private set; }

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
                PlayerMovementResolver.CreateMovementCommand(Player.Camera, Player.IsNoclipEnabled, commandFrame.MoveIntent),
                new Vector2(commandFrame.LookIntent.YawDelta, commandFrame.LookIntent.PitchDelta),
                AllowGameplayInput: true,
                AllowLook: true),
            tuning);
        long physicsStart = Stopwatch.GetTimestamp();
        Physics.StepSimulationFixed(deltaTime);
        _lastPhysicsStepMilliseconds = Stopwatch.GetElapsedTime(physicsStart).TotalMilliseconds;
        Player.SyncCameraFromBody(tuning);
        _physicsSync.SyncDynamicMapBodies(Map, Physics.DynamicMapCubeBodies);
        return playPrimaryUse;
    }

    public WorldSessionPresentationState CapturePresentationState(
        float maxRenderDistance,
        float presentationLeadSeconds = 0f,
        CameraViewMode cameraViewMode = CameraViewMode.FirstPerson)
    {
        // The returned state's camera stays a new instance per capture: callers (and tests) may keep
        // a state across captures and compare cameras, so only the intermediate camera is reused.
        Player.CopyPresentationCameraTo(_firstPersonCameraScratch, presentationLeadSeconds);
        Camera presentationCamera = ThirdPersonCameraRig.Resolve(
            _firstPersonCameraScratch,
            Player.GetWorldPosition(),
            cameraViewMode);

        long renderExportStart = Stopwatch.GetTimestamp();
        GeometryRenderExport visibleWorld = Map.CapturePresentationGeometryRenderExport(
            presentationCamera.Position,
            maxRenderDistance);
        double renderExportMilliseconds = Stopwatch.GetElapsedTime(renderExportStart).TotalMilliseconds;

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
                renderExportMilliseconds,
                _lastPhysicsStepMilliseconds,
                visibleWorld.CompatibilityCubes.Count + visibleWorld.DynamicCompatibilityCubes.Count,
                visibleWorld.Blocks.Count + visibleWorld.DynamicBlocks.Count,
                0,
                0,
                0,
                0,
                Physics.MapCubeBodies.Count));
    }

    public void RebuildMapForWorld(WorldDescriptor world)
    {
        ArgumentNullException.ThrowIfNull(world);
        RebuildMapForFloorSize(world.Width);
        World = world;
    }

    public void AdoptWorldDescriptor(WorldDescriptor world)
    {
        ArgumentNullException.ThrowIfNull(world);
        World = world;
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
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // The player's contact callback tests against the physics world, so it goes first.
        Player.Dispose();
        Physics.Dispose();
    }

    private Map CreateMap(int floorSize)
    {
        return new Map(
            Physics,
            generateStartupTerrain: true,
            width: floorSize,
            depth: floorSize,
            replicatedCubeBodies: true);
    }
}
