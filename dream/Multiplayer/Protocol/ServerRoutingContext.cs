using Dream.Multiplayer.Sessions;
using Dream.Simulation.Runtime;

namespace Dream.Multiplayer.Protocol;

public sealed class ServerRoutingContext
{
    public ServerRoutingContext(
        string connectionId,
        IAuthoritativeSessionHost? authority = null,
        IPlayerCommandInbox? commandInbox = null,
        IPlayerActionQueue? actionQueue = null,
        IServerSessionClaimRegistry? sessionClaims = null)
    {
        ConnectionId = connectionId;
        Authority = authority;
        CommandInbox = commandInbox;
        ActionQueue = actionQueue;
        SessionClaims = sessionClaims;
    }

    public string ConnectionId { get; }

    public IAuthoritativeSessionHost? Authority { get; }

    public IPlayerCommandInbox? CommandInbox { get; }

    public IPlayerActionQueue? ActionQueue { get; }

    public IServerSessionClaimRegistry? SessionClaims { get; }

    public ClientJoinRequestMessage? LastJoinRequest { get; set; }

    public PlayerCommandMessage? LastCommandMessage { get; set; }

    public PlayerActionMessage? LastActionMessage { get; set; }

    public List<IServerToClientMessage> OutboundMessages { get; } = [];

    // A connection gets at most one world resync per cooldown; requests inside it are dropped. It
    // is shorter than the client's 2 s retry interval (PlayScene), which runs on a different clock,
    // so a retry that arrives a little early because of jitter is still served.
    public static readonly TimeSpan WorldResyncCooldown = TimeSpan.FromSeconds(1.5);

    private bool _hasAcceptedWorldResync;
    private long _lastAcceptedWorldResyncTimestamp;

    // Set by WorldResyncRequestHandler and cleared by the host when it serves the resync after a
    // tick. Both run under the host's tick lock.
    public bool WorldResyncPending { get; private set; }

    // Marks the connection as wanting a world resync unless one was accepted within the cooldown.
    // Timestamps are TimeProvider/Stopwatch ticks at the given frequency.
    public bool TryRequestWorldResync(long nowTimestamp, long timestampFrequency)
    {
        if (_hasAcceptedWorldResync)
        {
            long cooldownTicks = (long)(WorldResyncCooldown.TotalSeconds * timestampFrequency);
            if (nowTimestamp - _lastAcceptedWorldResyncTimestamp < cooldownTicks)
            {
                return false;
            }
        }

        _hasAcceptedWorldResync = true;
        _lastAcceptedWorldResyncTimestamp = nowTimestamp;
        WorldResyncPending = true;
        return true;
    }

    // Returns whether a resync was pending, and clears it.
    public bool TryTakeWorldResyncRequest()
    {
        if (!WorldResyncPending)
        {
            return false;
        }

        WorldResyncPending = false;
        return true;
    }

    public void ClearOutboundMessages()
    {
        OutboundMessages.Clear();
    }
}
