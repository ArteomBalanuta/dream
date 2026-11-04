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
using Dream.Simulation.World.MapSources;
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
    private int _serverExperienceToNextLevel;
    private readonly PhysicsSyncSystem _physicsSync = new();
    private double _lastPhysicsStepMilliseconds;
    // Reused first-person camera for CapturePresentationState. It never leaves this type:
    // ThirdPersonCameraRig.Resolve copies it into the camera the presentation state carries.
    private readonly Camera _firstPersonCameraScratch = new(Vector3.Zero);
    private readonly IsometricFocus _isometricFocus = new();
    private readonly LocalPlayerRenderSmoother _renderSmoother = new();
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

    // The point the isometric camera looked at in the last capture; fog is measured from it.
    public Vector3 LastCameraFocus { get; private set; }

    // The local player's interpolated pose from the last capture; the model is drawn from it so it
    // moves with the camera. In first person it is the raw body pose.
    public LocalPlayerRenderPose LocalRenderPose { get; private set; }

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
        Vector3 bodyBeforeTick = Player.GetWorldPosition();
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
        _renderSmoother.RecordTick(bodyBeforeTick, Player.GetWorldPosition(), deltaTime);
        _physicsSync.SyncDynamicMapBodies(Map, Physics.DynamicMapCubeBodies);
        return playPrimaryUse;
    }

    public WorldSessionPresentationState CapturePresentationState(
        float maxRenderDistance,
        float presentationLeadSeconds = 0f,
        CameraViewMode cameraViewMode = CameraViewMode.FirstPerson,
        bool isDead = false,
        int respawnSeconds = 0,
        float frameSeconds = 0f)
    {
        // The returned state's camera stays a new instance per capture: callers (and tests) may keep
        // a state across captures and compare cameras, so only the intermediate camera is reused.
        Vector3 bodyNow = Player.GetWorldPosition();
        Vector3 renderBody = bodyNow;
        if (cameraViewMode == CameraViewMode.FirstPerson)
        {
            // First person keeps the velocity lead: the camera is the view and there is no model.
            Player.CopyPresentationCameraTo(_firstPersonCameraScratch, presentationLeadSeconds);
            LocalRenderPose = new LocalPlayerRenderPose(bodyNow, Player.Camera.HorizontalForward, false);
        }
        else
        {
            // Model and camera both follow the body interpolated between ticks, so they cannot
            // drift apart and a constant-speed walk advances evenly every frame.
            LocalRenderPose = _renderSmoother.Resolve(
                bodyNow,
                presentationLeadSeconds / SimulationClock.FixedDeltaSeconds,
                Player.Camera.HorizontalForward,
                frameSeconds);
            renderBody = LocalRenderPose.Position;
            Player.CopyPresentationCameraTo(_firstPersonCameraScratch, 0f);
            _firstPersonCameraScratch.Position += renderBody - bodyNow;
        }

        Camera presentationCamera;
        if (cameraViewMode == CameraViewMode.Isometric)
        {
            LastCameraFocus = _isometricFocus.Advance(_firstPersonCameraScratch.Position, frameSeconds);
            presentationCamera = new Camera(LastCameraFocus);
            IsometricView.ResolveInto(LastCameraFocus, presentationCamera);
        }
        else
        {
            presentationCamera = ThirdPersonCameraRig.Resolve(
                _firstPersonCameraScratch,
                renderBody,
                cameraViewMode);
        }

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
            _serverExperienceToNextLevel > 0
                ? _serverExperienceToNextLevel
                : _combatFormulaService.EvaluateExperienceToNextLevel(
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
                Physics.MapCubeBodies.Count),
            Player.Combatant.BaseStats,
            Player.Combatant.EffectiveArmor,
            isDead,
            respawnSeconds);
    }

    public void RebuildMapForWorld(WorldDescriptor world, IMapSource? source = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (source != null)
        {
            RebuildMapFromSource(source);
            World = world;
            return;
        }

        if (world.MapSource != WorldMapSource.Generated)
        {
            throw new UnsupportedWorldException(
                $"The world \"{world.Name}\" is {world.MapSource} and needs its terrain to be rebuilt.");
        }

        RebuildMapForDescriptor(world);
        World = world;
    }

    public void AdoptWorldDescriptor(WorldDescriptor world)
    {
        ArgumentNullException.ThrowIfNull(world);
        World = world;
    }

    // A stored world's map. No CopyPlacedCubesTo: it would replay the old map's removals onto a
    // different source, and the join's snapshot follows immediately.
    private void RebuildMapFromSource(IMapSource source)
    {
        Vector3 currentPlayerPosition = Player.GetWorldPosition();
        Physics.ResetMapBodies();
        Map rebuiltMap = new(source, Physics, replicatedCubeBodies: true);
        Map = rebuiltMap;
        Player.BindMap(rebuiltMap);
        Enemies.ClampToMapBounds(rebuiltMap);
        Player.SetWorldPosition(new Vector3(
            Math.Clamp(currentPlayerPosition.X, 0.5f, rebuiltMap.Width - 0.5f),
            Math.Clamp(currentPlayerPosition.Y, 1.5f, rebuiltMap.Height - 0.5f),
            Math.Clamp(currentPlayerPosition.Z, 0.5f, rebuiltMap.Depth - 0.5f)));
    }

    // A generated world is rebuilt from its own generator, not from its size: the descriptor's key
    // and version decide the distribution, and the server built its map from the same pair. The
    // seed is server-only, and every registered generator is seed-independent, so 0 stands in for
    // it (see WorldGeneratorCatalog).
    private void RebuildMapForDescriptor(WorldDescriptor world)
    {
        Vector3 currentPlayerPosition = Player.GetWorldPosition();
        Map previousMap = Map;
        Physics.ResetMapBodies();
        Map rebuiltMap = new(MapSources.ForWorld(world, seed: 0UL), Physics, replicatedCubeBodies: true);
        previousMap.CopyPlacedCubesTo(rebuiltMap);
        Map = rebuiltMap;
        Player.BindMap(rebuiltMap);
        Enemies.ClampToMapBounds(rebuiltMap);
        Player.SetWorldPosition(new Vector3(
            Math.Clamp(currentPlayerPosition.X, 0.5f, rebuiltMap.Width - 0.5f),
            Math.Clamp(currentPlayerPosition.Y, 1.5f, rebuiltMap.Height - 0.5f),
            Math.Clamp(currentPlayerPosition.Z, 0.5f, rebuiltMap.Depth - 0.5f)));
    }

    public void SetServerExperienceToNextLevel(int required) => _serverExperienceToNextLevel = required;

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
