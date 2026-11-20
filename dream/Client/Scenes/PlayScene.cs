using OpenTK.Windowing.Common;
using Vector3 = OpenTK.Mathematics.Vector3;
using Dream.Application;
using Dream.Client.Input;
using Dream.Client.Navigation;
using Dream.Client.Networking;
using Dream.Client.Presentation;
using Dream.Client.Replication;
using Dream.Client.Startup;
using Dream.Client.World;
using Dream.Configuration;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Rendering.Core;
using Dream.Rendering.Hud;
using Dream.Rendering.Ui;
using Dream.Rendering.ViewModels;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;

namespace Dream.Client.Scenes;


// PlayScene is currently the desktop gameplay orchestration root.
// Authoritative world/player/physics ownership is expected to move out of this scene into
// a dedicated runtime, while client presentation concerns remain scene-owned.
public partial class PlayScene : IDisposable
{
    private const string LoopbackRemotePlayersEnvironmentVariable = "DREAM_LOOPBACK_REMOTE_PLAYERS";
    private const string RenderPerformanceLogEnvironmentVariable = "DREAM_RENDER_PERF_LOG";
    private static readonly TimeSpan DiagnosticsLogInterval = TimeSpan.FromSeconds(1);
    // Model uploads per frame. Each is one model's GL buffers and textures.
    private const int ModelUploadsPerFrame = 2;

    // Client-local presentation state that should remain scene-owned after the runtime split.
    private readonly Render _render;
    private readonly GameSettings _settings;
    private readonly DesktopClientConfig? _desktopClientConfig;
    private readonly Action? _persistDesktopClientConfig;
    private readonly ILocalServerHandle? _localServer;
    private readonly PlayerViewModelController _viewModelController;
    private readonly SceneEventHub _events = new();
    private readonly CombatFeedbackSystem _combatFeedback;
    private readonly AuthoritativeActionFeedbackSystem _actionFeedback;
    private readonly MultiplayerDiagnostics _diagnostics;
    private readonly ClientProtocolSession _clientProtocol;
    private readonly ClientScenePresenter _scenePresenter;
    private readonly RemoteClientSession _remoteClient;
    private readonly GameplayInputResumeFilter _gameplayInputResumeFilter = new();
    private readonly PlayerCommandOneShotInputFilter _commandInputFilter = new();
    private readonly bool _renderPerformanceLoggingEnabled;
    // Wave I: the local respawn countdown (display only) and the loot lines of inventory pushes.
    private readonly ClientDeathTracker _deathTracker = new();
    private readonly LootFeedbackSystem _lootFeedback = new();

    // The client's replicated world and predicted local player; authority lives on the server.
    // PlayScene remains the desktop composition root plus client presentation owner.
    private readonly ClientWorldSession _runtime;
    // One command per 64 Hz prediction tick, its sequence the tick (NETR-4).
    private readonly ClientCommandTickStream _commandTicks = new(PlayerSessionId.LocalDefault);
    private readonly bool _enableLoopbackRemotePlayers;
    private readonly ClientActionSequenceTracker _actionSequences = new();
    private readonly ClientPredictionReconciler _predictionReconciler = new();
    private readonly ServerTickClock _serverTickClock = new();
    private bool _wasSettingsTogglePressed;
    private bool _wasInventoryTogglePressed;
    private bool _wasAdminHudTogglePressed;
    private bool _localPlayerWasMoving;
    private ClickMoveController? _clickMoveController;
    private CursorAimController? _cursorAimController;
    private EnemyHoverProbe? _enemyHoverProbe;
    // W/A/S/D of the current frame in camera space; projected to forward/strafe per tick, once that
    // tick's facing is known (PlayScene.Update).
    private OpenTK.Mathematics.Vector2 _tickCameraAxes;
    private double _clientPresentationTimeSeconds;
    private double _renderPerformanceLogSeconds;
    private double _lastPresentedFrameMilliseconds;
    private double _lastSwapBuffersMilliseconds;
    private double _lastHudDrawMilliseconds;
    // GPU time per section, read back a few frames late (created on the first Render, with the
    // context current), and the GL call counts of the previous complete frame.
    private GpuFrameTimer? _gpuTimer;
    private RenderCounterSnapshot _lastFrameCounters;
    private PlayerInventorySnapshot? _inventory;
    // How many inventory snapshots have been applied; the capture script waits on it.
    private int _inventoryApplyCount;
    private bool _disposed;

    // Inputs and output of the prediction tick for the current frame. The tick stream calls a
    // static lambda with this scene as its state, so these are fields rather than captured locals.
    private PlayerSimulationTuning _tickTuning;
    private bool _tickPrimaryUseTriggered;

    // The part of Render before the world draw: GL.Clear, presentation capture and the
    // remote-player states. Reported in the frame snapshot and the render-perf log line.
    private double _lastClearMilliseconds;
    private double _lastPresentationCaptureMilliseconds;
    private double _lastRemotePlayerStatesMilliseconds;

    // Diagnostics keys for the remote session, rebuilt only when the session id changes.
    private string? _diagnosticsKeySessionId;
    private string _snapshotLogKey = string.Empty;
    private string _commandLogKey = string.Empty;
    private string _presenterStateLogKey = string.Empty;

    private double RemotePlayerRenderTick => _serverTickClock.RenderTick;

    // The classic HUD draws its own sprite cursor, so the OS cursor hides (it can still leave the
    // window; a drop outside cancels). Settings and the ImGui surface keep the normal cursor.
    // The loading screen shows the plain cursor (the HUD, and its sprite cursor, are not interactive under it).
    public CursorState DesiredCursorState =>
        LoadingOverlayVisible
            ? CursorState.Normal
            : ClassicCursorPolicy.Decide(
                _render.ClassicHudCursorActive,
                _settings.SettingsVisible,
                _windowFocused,
                _settings.CurrentRuntimeState.DesiredCursorState);

    // Created on first use: it needs this scene for its host, which a field initialiser cannot take.
    private ClickMoveController _clickMove =>
        _clickMoveController ??= new ClickMoveController(new ClickMoveHost(this), new NavPathing());

    private EnemyHoverProbe _enemyHover =>
        _enemyHoverProbe ??= new EnemyHoverProbe(new ClickMoveHost(this));

    private CursorAimController _cursorAim =>
        _cursorAimController ??= new CursorAimController(new CursorAimHost(this));

    // Set by Render each frame; true until the first one.
    private bool _windowFocused = true;

    public Vector3 LastReportedPlayerPosition => _runtime.Player.LastReportedPosition;

    public string ConnectedServerEndpoint => _remoteClient.Options.EndpointLabel;

    internal SceneEventHub Events => _events;

    // Click-to-move reads the player and the map through this; the pick uses last frame's camera.
    private sealed class ClickMoveHost : IClickMoveHost
    {
        private readonly PlayScene _scene;
        private Dream.Simulation.World.Map? _navMap;
        private MapNavWorld? _nav;

        public ClickMoveHost(PlayScene scene)
        {
            _scene = scene;
        }

        public INavWorld? World
        {
            get
            {
                Dream.Simulation.World.Map map = _scene._runtime.Map;
                if (!ReferenceEquals(map, _navMap))
                {
                    _navMap = map;
                    _nav = new MapNavWorld(map);
                }

                return _nav;
            }
        }

        public Vector3 BodyCentre => _scene._runtime.Player.GetWorldPosition();

        public Vector3 Eye => _scene._runtime.Player.Camera.Position;

        public bool NoclipActive => _scene._runtime.Player.IsNoclipEnabled;

        public PlayerInteractionMode Mode => _scene._runtime.InteractionMode;

        public bool IsEnemyAt(OpenTK.Mathematics.Vector2 screenPosition)
        {
            Dream.Simulation.Players.Camera? camera = _scene._render.LastWorldCamera;
            INavWorld? world = World;
            if (camera is null || world is null)
            {
                return false;
            }

            return WorldCursorPicker.TryBuildRay(
                    camera.GetViewMatrix(),
                    _scene._render.ProjectionMatrix,
                    screenPosition,
                    _scene._render.ViewportSize,
                    out Vector3 origin,
                    out Vector3 direction)
                && WorldCursorPicker.PickHoveredEnemy(world, _scene._runtime.Enemies, origin, direction) is not null;
        }

        public bool TryPick(OpenTK.Mathematics.Vector2 screenPosition, out ClickPick pick)
        {
            pick = default;
            Dream.Simulation.Players.Camera? camera = _scene._render.LastWorldCamera;
            INavWorld? world = World;
            if (camera is null || world is null)
            {
                return false;
            }

            if (!WorldCursorPicker.TryBuildRay(
                camera.GetViewMatrix(),
                _scene._render.ProjectionMatrix,
                screenPosition,
                _scene._render.ViewportSize,
                out Vector3 origin,
                out Vector3 direction))
            {
                return false;
            }

            pick = WorldCursorPicker.Pick(world, _scene._runtime.Enemies, origin, direction);
            return pick.Kind != ClickPickKind.None;
        }

        public void SetFacing(Vector3 direction) => _scene._runtime.Player.SetFacing(direction);

        public Vector3 Facing => _scene._runtime.Player.Camera.Front;

        public bool AimOwnsFacing => _scene._cursorAim.OwnsFacing;
    }

    // The cursor aim reads the player and last frame's camera through this.
    private sealed class CursorAimHost : ICursorAimHost
    {
        private readonly PlayScene _scene;

        public CursorAimHost(PlayScene scene)
        {
            _scene = scene;
        }

        public Vector3 BodyCentre => _scene._runtime.Player.GetWorldPosition();

        public PlayerInteractionMode Mode => _scene._runtime.InteractionMode;

        public bool TryBuildRay(OpenTK.Mathematics.Vector2 screenPosition, out Vector3 origin, out Vector3 direction)
        {
            origin = default;
            direction = default;
            Dream.Simulation.Players.Camera? camera = _scene._render.LastWorldCamera;
            return camera is not null && WorldCursorPicker.TryBuildRay(
                camera.GetViewMatrix(),
                _scene._render.ProjectionMatrix,
                screenPosition,
                _scene._render.ViewportSize,
                out origin,
                out direction);
        }
    }
}
