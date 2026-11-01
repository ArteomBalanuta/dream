using OpenTK.Windowing.Common;
using Vector3 = OpenTK.Mathematics.Vector3;
using Dream.Application;
using Dream.Client.Input;
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
    private bool _wasCameraViewTogglePressed;
    private bool _wasAdminHudTogglePressed;
    private bool _localPlayerWasMoving;
    private readonly CursorChangeLookHoldoff _lookHoldoff = new();
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

    // The classic inventory draws its own cursor, so the OS cursor hides (it can still leave the
    // window; a drop outside cancels). Settings and the ImGui surface keep the normal cursor.
    public CursorState DesiredCursorState =>
        _render.ClassicHudCursorActive && _settings.InventoryVisible && !_settings.SettingsVisible
            ? CursorState.Hidden
            : _settings.CurrentRuntimeState.DesiredCursorState;

    public Vector3 LastReportedPlayerPosition => _runtime.Player.LastReportedPosition;

    public string ConnectedServerEndpoint => _remoteClient.Options.EndpointLabel;

    internal SceneEventHub Events => _events;
}
