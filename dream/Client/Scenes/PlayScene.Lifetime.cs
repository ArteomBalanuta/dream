using System.Runtime.ExceptionServices;
using Vector3 = OpenTK.Mathematics.Vector3;
using Dream.Application;
using Dream.Client.Networking;
using Dream.Client.Presentation;
using Dream.Client.Replication;
using Dream.Client.Startup;
using Dream.Client.World;
using Dream.Configuration;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Rendering.Core;
using Dream.Rendering.Hud;
using Dream.Rendering.ViewModels;

namespace Dream.Client.Scenes;

public partial class PlayScene
{
    public PlayScene(
        GameSettings settings,
        RemoteClientSession remoteClient,
        DesktopClientConfig? clientConfig = null,
        Action? persistDesktopClientConfig = null,
        bool enableLoopbackRemotePlayers = false,
        ILocalServerHandle? localServer = null)
    {
        _settings = settings;
        _desktopClientConfig = clientConfig;
        _persistDesktopClientConfig = persistDesktopClientConfig;
        _localServer = localServer;
        _enableLoopbackRemotePlayers = enableLoopbackRemotePlayers;
        _remoteClient = remoteClient ?? throw new ArgumentNullException(nameof(remoteClient));
        // A send stalled past the server's repeat window makes the prediction ticks idle, as the
        // server does (ClientCommandTickStream); so does an ack lag past the round trip (a network stall).
        _commandTicks.ObserveSendBacklog(() => _remoteClient.IsConnected ? _remoteClient.PendingCommandCount : 0);
        _commandTicks.ObserveAcknowledgedSequence(() => _remoteClient.IsConnected ? ClientCommandTickStream.AcknowledgedSequence(_clientProtocol.Context.LastSnapshot, _remoteClient.SessionId) : null);
        _renderPerformanceLoggingEnabled = IsRenderPerformanceLoggingEnabled();
        // The desktop scene owns a client world (replicated map + predicted player); the server
        // owns authority. PlayScene remains the composition root for presentation systems.
        _viewModelController = new PlayerViewModelController(ViewModelCatalog.CreateDefault(), _settings);
        _clientProtocol = ClientProtocolSession.CreateDefault();

        Vector3 playerStartPosition = new Vector3(5, 1.5f, 5);
        _runtime = new ClientWorldSession(
            _settings.FloorSize,
            playerStartPosition,
            _events);
        if (_desktopClientConfig != null)
        {
            _runtime.Player.Combatant.ApplyStats(_desktopClientConfig.ToPlayerCombatStats());
        }

        _scenePresenter = new ClientScenePresenter(_remoteClient.SessionId);
        _render = new Render(_settings);
        PreloadModels();
        _combatFeedback = new CombatFeedbackSystem(_events, new FloatingCombatTextSystem());
        _actionFeedback = new AuthoritativeActionFeedbackSystem(_events);
        _diagnostics = new MultiplayerDiagnostics();

        NetworkDiagnosticsLog.Write(
            "client.scene.start",
            $"remote=True endpoint={ConnectedServerEndpoint} localPresenterSession={_remoteClient.SessionId}");
    }

    // Starts the background decodes of the shared models the scene draws, so they are usually
    // ready within the first frames. The player model is not preloaded here: players draw
    // per-instance models, which LocalPlayerRenderer and RemotePlayerRenderer request themselves,
    // so a shared player model would be uploaded and never drawn.
    private static void PreloadModels()
    {
        ModelAssets.Preload(
            ModelAssets.WolfPath,
            ModelAssets.GrassCubePath,
            ModelAssets.DirtCubePath,
            ModelAssets.StoneCubePath);
        IReadOnlyList<ViewModelDefinition> viewModels = ViewModelAssets.BuiltInDefinitions;
        for (int i = 0; i < viewModels.Count; i++)
        {
            ModelAssets.Preload(viewModels[i].ModelPath);
        }
    }

    // Runs on the render thread while the GL context is still current (Game.OnUnload disposes the
    // scene before base.OnUnload). GL is released first: Render, then the shared model cache, which
    // world cubes, enemies and viewmodels only reference for drawing, so it must outlive the last
    // draw and Render's renderers. The cache is cleared, so a later scene reloads the models.
    // Each step runs even if an earlier one throws, so a network failure cannot skip GL cleanup
    // or the reverse; the failures are rethrown at the end.
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        List<Exception>? failures = null;
        RunDisposeStep(_render.Dispose, ref failures);
        RunDisposeStep(DisposeGpuTimer, ref failures);
        RunDisposeStep(DisposeSky, ref failures);
        RunDisposeStep(ModelAssets.DisposeAll, ref failures);
        RunDisposeStep(_combatFeedback.Dispose, ref failures);
        RunDisposeStep(_actionFeedback.Dispose, ref failures);
        RunDisposeStep(_runtime.Dispose, ref failures);
        RunDisposeStep(DisposeRemoteClient, ref failures);

        if (failures == null)
        {
            return;
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        throw new AggregateException(failures);
    }

    private void DisposeGpuTimer()
    {
        _gpuTimer?.Dispose();
        _gpuTimer = null;
    }

    // The sky owns its own program and VAO, so it is released with the other GL resources.
    private void DisposeSky()
    {
        _sky?.Dispose();
        _sky = null;
    }

    private static void RunDisposeStep(Action step, ref List<Exception>? failures)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            (failures ??= []).Add(ex);
        }
    }

    private void DisposeRemoteClient()
    {
        _remoteClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
