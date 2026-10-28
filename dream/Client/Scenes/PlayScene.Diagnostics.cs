using OpenTK.Windowing.Common;
using Dream.Client.Presentation;
using Dream.Multiplayer.Diagnostics;
using Dream.Rendering.Actors;
using Dream.Rendering.Hud;
using Dream.Simulation.Players;

namespace Dream.Client.Scenes;

public partial class PlayScene
{
    private static bool IsRenderPerformanceLoggingEnabled()
    {
        string? value = Environment.GetEnvironmentVariable(RenderPerformanceLogEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private void LogRenderPerformanceIfEnabled(FrameEventArgs args, FramePerformanceSnapshot performance, bool windowFocused)
    {
        if (!_renderPerformanceLoggingEnabled || !performance.HasCounters)
        {
            return;
        }

        _renderPerformanceLogSeconds += args.Time;
        if (_renderPerformanceLogSeconds < 1d)
        {
            return;
        }

        _renderPerformanceLogSeconds = 0d;
        Console.WriteLine(
            $"[render-perf] frame={args.Time * 1000d:0.00}ms pos={LastReportedPlayerPosition} " +
            $"focused={windowFocused} settings={_settings.SettingsVisible} inventory={_settings.InventoryVisible} " +
            string.Join(" | ", performance.ToHudLines()) +
            $" | clear={_lastClearMilliseconds:0.00}ms capture={_lastPresentationCaptureMilliseconds:0.00}ms " +
            $"remoteStates={_lastRemotePlayerStatesMilliseconds:0.00}ms");
    }

    // Diagnostics keys are only needed while diagnostics are on; they are rebuilt only when the
    // remote session id differs from the one they were built for.
    private void RefreshDiagnosticsKeys()
    {
        string sessionId = _remoteClient.SessionId;
        if (string.Equals(sessionId, _diagnosticsKeySessionId, StringComparison.Ordinal))
        {
            return;
        }

        _diagnosticsKeySessionId = sessionId;
        _snapshotLogKey = $"client.apply.snapshot.{sessionId}";
        _commandLogKey = $"client.scene.command.{sessionId}";
        _presenterStateLogKey = $"client.presenter.state.{sessionId}";
    }

    private void LogPresenterDiagnostics(string source)
    {
        if (!NetworkDiagnosticsLog.IsEnabled)
        {
            return;
        }

        RefreshDiagnosticsKeys();
        if (!NetworkDiagnosticsLog.ShouldLogEvery(_presenterStateLogKey, DiagnosticsLogInterval))
        {
            return;
        }

        ClientScenePresenterDebugState state = _scenePresenter.CaptureDebugState(
            RemotePlayerRenderTick);
        NetworkDiagnosticsLog.Write(
            "client.presenter.state",
            $"source={source} local={state.LocalSessionId ?? "<none>"} roster={state.RosterCount}[{string.Join(", ", state.RosterSessions)}] " +
            $"remoteEntries={state.RemoteEntryCount}[{string.Join(", ", state.RemoteSessions)}] sampled={state.SampledRemoteCount}");
    }

    private static void LogRemotePlayerRenderDiagnostics(
        Camera camera,
        IReadOnlyList<RemotePlayerPresentationState> remotePlayerStates,
        RemotePlayerRenderStats stats)
    {
        if (!NetworkDiagnosticsLog.ShouldLogEvery("client.render.remote", TimeSpan.FromSeconds(1)))
        {
            return;
        }

        string sessions = string.Join(
            ", ",
            remotePlayerStates.Select(player =>
                $"{player.SessionId}#{player.ActorId}/{player.DisplayName}@{NetworkDiagnosticsLog.FormatVector(player.Pose.Position)} alive={player.IsAlive}"));
        NetworkDiagnosticsLog.Write(
            "client.render.remote",
            $"camera={NetworkDiagnosticsLog.FormatVector(camera.Position)} input={stats.InputCount} alive={stats.AliveCount} " +
            $"visible={stats.VisibleCount} cachedModels={stats.ModelCount} players=[{sessions}]");
    }
}
