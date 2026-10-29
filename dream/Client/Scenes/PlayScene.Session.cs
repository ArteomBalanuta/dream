using Dream.Application;
using Dream.Client.Networking;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Rendering.Hud;

namespace Dream.Client.Scenes;

public partial class PlayScene : ISessionEndSource
{
    // Why the session ended (a lost connection, a server silent for 8 s, a rejected join, or a
    // join that timed out), or null while it is live. Update reports it once on the console and in
    // the network log; Game then disposes this scene and returns to the connection screen, which
    // shows it (NETR-2).
    public string? SessionEndReason => _remoteClient.IsConnected
        ? null
        : _remoteClient.DisconnectReason ?? "Disconnected from the server.";

    private bool _sessionEndReported;
    private Action<string>? _endSessionAsProtocolError;

    // The endpoint and name this scene's session used, so the connection screen Game returns to
    // after the session ends is filled in for a reconnect.
    public RemoteClientOptions SessionOptions => _remoteClient.Options;

    // The HUD's view of a world transfer in progress (a join or a resync). Remote actors freeze
    // until it ends, because every message is held until its final chunk; local input and
    // prediction deliberately carry on, as they already work against the held state. Read once
    // per frame; allocates nothing.
    internal static WorldTransferHudState CaptureWorldTransferHudState(RemoteClientSession session)
    {
        return session.TryGetWorldTransferProgress(out long bytesReceived, out int chunksReceived, out TimeSpan elapsed)
            ? new WorldTransferHudState(true, bytesReceived, chunksReceived, elapsed)
            : WorldTransferHudState.None;
    }

    private void DrainRemoteTransportMessages()
    {
        // Once a message ended the session (a protocol error), the rest of the queue is not applied.
        while (_remoteClient.IsConnected && _remoteClient.TryDequeueInbound(out IServerToClientMessage? message) && message != null)
        {
            ApplyReplicatedMessage(message);
        }

        if (!_sessionEndReported && !_remoteClient.IsConnected)
        {
            ReportSessionEnd();
        }
    }

    // Once per scene, off the steady-state frame path.
    private void ReportSessionEnd()
    {
        _sessionEndReported = true;
        string reason = SessionEndReason ?? "Disconnected from the server.";
        Console.WriteLine($"[session] ended: {reason}");
        NetworkDiagnosticsLog.Write(
            "client.scene.session-end",
            $"session={_remoteClient.SessionId} reason={reason}");
    }
}
