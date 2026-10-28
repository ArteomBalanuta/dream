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

    // Join throttle (NETR-8). After a join from this connection is rejected because its session id
    // or player name is held by a live connection, further joins from it are dropped unanswered
    // (answering each would turn join spam into reply spam) until the throttle interval passes.
    // The interval starts at JoinThrottleInitialInterval and doubles on every further live-holder
    // rejection up to JoinThrottleMaxInterval; an accepted join resets it. A legitimate client
    // never meets it: it ends its session on such a rejection and comes back on a new connection.
    // Everything here runs under the host's tick lock.
    public static readonly TimeSpan JoinThrottleInitialInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan JoinThrottleMaxInterval = TimeSpan.FromSeconds(10);

    private long _joinThrottleUntilTimestamp;
    private long _joinThrottleIntervalTicks;

    // Set by the claim registry while it handles this connection's join, when a live connection
    // holds the requested session id or player name. The host reads and clears it after the join.
    public bool JoinConflictsWithLiveHolder { get; set; }

    // Joins dropped by the current throttle interval.
    public int DroppedJoinAttempts { get; private set; }

    public bool IsJoinThrottled(long nowTimestamp)
    {
        return _joinThrottleIntervalTicks != 0 && nowTimestamp < _joinThrottleUntilTimestamp;
    }

    // Counts a dropped join and returns how many the current interval has dropped so far.
    public int RecordDroppedJoinAttempt()
    {
        return ++DroppedJoinAttempts;
    }

    // Starts the next throttle interval (doubled, capped) and returns its length in timestamp ticks.
    public long ThrottleJoins(long nowTimestamp, long timestampFrequency)
    {
        long initialTicks = (long)(JoinThrottleInitialInterval.TotalSeconds * timestampFrequency);
        long maxTicks = (long)(JoinThrottleMaxInterval.TotalSeconds * timestampFrequency);
        _joinThrottleIntervalTicks = _joinThrottleIntervalTicks == 0
            ? initialTicks
            : Math.Min(_joinThrottleIntervalTicks * 2, maxTicks);
        _joinThrottleUntilTimestamp = nowTimestamp + _joinThrottleIntervalTicks;
        DroppedJoinAttempts = 0;
        return _joinThrottleIntervalTicks;
    }

    public void ResetJoinThrottle()
    {
        _joinThrottleIntervalTicks = 0;
        _joinThrottleUntilTimestamp = 0;
        DroppedJoinAttempts = 0;
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
