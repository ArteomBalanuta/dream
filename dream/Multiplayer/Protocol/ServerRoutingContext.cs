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

    // Liveness for stale-session takeover (NETR-8): the host's TimeProvider timestamp of the last
    // inbound frame seen from this connection. The host marks it for every routed message (receive
    // thread, under the tick lock); the service also reports the transport's frame count, which
    // includes the client's empty keepalive frames, through ObserveInboundFrameCount (tick thread).
    // A keepalive is therefore stamped with the observation's time, not its arrival time: up to a
    // tick late, which only makes the connection look more alive. The host bounds silence by its
    // last observation sweep, so a tick stall does not age this value (DedicatedServerHost).
    private long _lastInboundTimestamp;
    private long _observedInboundFrameCount;

    public long LastInboundTimestamp => Volatile.Read(ref _lastInboundTimestamp);

    public void MarkInbound(long timestamp)
    {
        Volatile.Write(ref _lastInboundTimestamp, timestamp);
    }

    // Marks the connection alive when its transport's received-frame count moved since the last
    // observation. Returns whether it did. Allocation-free.
    public bool ObserveInboundFrameCount(long framesReceived, long timestamp)
    {
        if (Interlocked.Exchange(ref _observedInboundFrameCount, framesReceived) == framesReceived)
        {
            return false;
        }

        MarkInbound(timestamp);
        return true;
    }

    // Join throttle (NETR-8). After a join is rejected because its session id or player name is
    // held by a live connection, further joins from the same source (the remote IP address, see
    // Dream.Server.JoinThrottleTable) for the same contested session id or name are throttled
    // until the throttle interval passes, across reconnects. The interval starts at
    // JoinThrottleInitialInterval and doubles on every further live-holder rejection up to
    // JoinThrottleMaxInterval; an accepted join resets it. A throttled join is never claimed or
    // queued, so it costs no persisted-player prefetch. A connection that was already answered
    // with a rejection gets nothing back (answering each would turn join spam into reply spam);
    // a fresh connection gets one JoinRejected saying when to retry. A legitimate client only
    // meets it when it rejoins a seat its own still-live old connection holds.
    public static readonly TimeSpan JoinThrottleInitialInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan JoinThrottleMaxInterval = TimeSpan.FromSeconds(10);

    // Set by the claim registry while it handles this connection's join, when a live connection
    // holds the requested session id (JoinConflictSessionId) or player name (JoinConflictPlayerName).
    // The host reads and clears both after the join. Under the host's tick lock.
    public string? JoinConflictSessionId { get; set; }

    public string? JoinConflictPlayerName { get; set; }

    // Whether this connection has been answered with a join rejection (a live-holder conflict or
    // a throttle), so further throttled joins from it are dropped unanswered. Under the tick lock.
    public bool JoinRejectionSent { get; set; }

    public void ClearJoinConflict()
    {
        JoinConflictSessionId = null;
        JoinConflictPlayerName = null;
    }

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
