using System.Collections.Concurrent;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Characters;
using Dream.Simulation.Combat;
using Dream.Simulation.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Server;

// A reply produced for a connection outside HandleClientMessage, such as a deferred join's
// JoinAccepted or JoinRejected, or a world resync served after a tick.
public readonly record struct PendingConnectionReply(string ConnectionId, IServerToClientMessage Message);

public sealed partial class DedicatedServerHost : IDisposable, IServerSessionClaimRegistry
{
    private static readonly PlayerSimulationTuning DefaultServerTuning = PlayerSimulationTuning.AuthoritativeDefault;

    // ConcurrentDictionary enumeration order follows string hash codes, which .NET randomizes per
    // process, so joined sessions are put in ordinal session-id order before they drive the tick.
    private static readonly Comparison<PlayerSessionId> CompareSessionIdsOrdinal =
        static (left, right) => string.CompareOrdinal(left.Value, right.Value);

    // Joins applied at a tick boundary go in ordinal requested-session-id order, then connection id.
    private static readonly Comparison<PendingJoin> ComparePendingJoinsOrdinal =
        static (left, right) =>
        {
            int bySession = string.CompareOrdinal(left.Request.RequestedSessionId, right.Request.RequestedSessionId);
            return bySession != 0 ? bySession : string.CompareOrdinal(left.ConnectionId, right.ConnectionId);
        };

    // How many ticks a deferred join may wait for its player's persisted state to load (30 s). The
    // load normally finishes within a few ticks; this only bounds a database that stays locked.
    // A join past the bound, or whose load failed twice, is rejected with JoinRetryRejectionReason
    // instead of reading the database on the tick.
    internal const int MaxJoinPrefetchDeferralTicks = 30 * SimulationClock.AuthoritativeTicksPerSecond;

    public const string JoinRetryRejectionReason =
        "The server could not load this player's saved data in time; retry the join.";

    // Stale-session takeover (NETR-8). A join for a session (or a player name) held by another
    // connection takes it over when that connection has sent no frame for TakeoverSilence, so a
    // crashed client's rejoin does not wait for the 10 s idle timeout. A connection heard from
    // within the window keeps its session and the join is rejected, so a second client cannot
    // hijack a live player. Identity is unauthenticated (see TryClaimSession), so the window trades
    // crash-rejoin speed against how easily a network stall lets another client take a seat.
    //
    // One window covers both matches. A shorter one for a repeated session id would buy nothing:
    // the desktop client picks a fresh "client-<guid>" on every connect, so its real crash rejoin
    // is always a name match, while session ids are broadcast to every client in snapshots, so a
    // shorter session-id window would only let a hijacker bypass the name window. A healthy idle
    // client sends a frame at least every RemoteClientSession.KeepaliveInterval (1 s; gaps reach
    // about 1.5 s), so 8 s of silence is not a hiccup, and it stays under the 10 s idle release, so
    // a crashed client still gets back sooner.
    public static readonly TimeSpan TakeoverSilence = TimeSpan.FromSeconds(8);

    public const string SessionInUseRejectionReason =
        "Session already in use: another connection is still active for it.";

    // Sent to a connection whose seat a same-account join took over, just before its socket closes.
    public const string OpenedElsewhereRejectionReason = "This character was opened somewhere else.";

    public const string JoinThrottledRejectionReason =
        "Too many join attempts for this name.";

    public const string NewAccountRejectionReason =
        "Too many new players have joined from this address. Existing characters can still join.";

    private readonly IProtocolRouter _router;
    private readonly IAuthoritativeSessionHost? _authority;
    private readonly ITickDrivenAuthoritativeSessionHost? _tickDrivenAuthority;
    private readonly BufferedPlayerCommandInbox _commandInbox = new();
    private readonly PlayerActionQueue _actionQueue = new();
    private readonly ServerTickRunner _tickRunner = new();
    private readonly ConcurrentDictionary<string, ServerRoutingContext> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _sessionClaims = new(StringComparer.Ordinal);
    private readonly List<SessionVitals> _vitalsScratch = [];
    private readonly List<SessionServerMessage> _sessionMessageScratch = [];
    private readonly Func<PlayerSessionId, bool> _isSessionClaimed;
    private readonly ConcurrentDictionary<string, string> _connectionClaims = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<AuthoritativeActionResult> _pendingActionResults = new();
    private readonly ConcurrentQueue<PendingConnectionReply> _pendingConnectionReplies = new();
    private readonly bool _deferJoinsToTick;
    // Join requests received since the last tick, touched only under _authoritySync. The tick
    // swaps in _applyingJoins before routing, because a failed join releases its connection and
    // ReleaseConnection edits _pendingJoins.
    private List<PendingJoin> _pendingJoins = [];
    private List<PendingJoin> _applyingJoins = [];
    private readonly object _authoritySync = new();

    // Tick scratch, touched only under _authoritySync. The context list mirrors _connections and
    // is rebuilt only after a connection is added or removed.
    private readonly List<ServerRoutingContext> _connectionContexts = [];
    private readonly List<PlayerSessionId> _joinedSessionIds = [];
    private readonly List<PlayerCommandFrame> _tickCommands = [];
    private readonly List<QueuedPlayerAction> _tickActions = [];
    private readonly List<SessionInventoryPush> _inventoryPushScratch = [];
    private readonly HashSet<string> _heldPushSessions = new(StringComparer.Ordinal);
    private int _connectionsChanged = 1;
    // Set under _authoritySync by Dispose. Every entry point that touches the runtime checks it
    // under the same lock, so a late ReleaseConnection (a connection loop's finally on a pool
    // thread), message or tick cannot touch the Bullet world while or after it is disposed.
    private bool _disposed;
    private readonly BackgroundCheckpointWriter? _checkpointWrites;
    private double _checkpointElapsedSeconds;
    // Connections whose session a join took over (NETR-8). The host already released them; the
    // service drains the queue and closes their transports. The set drops any late message from
    // one of them, so it cannot come back as a ghost context, including one still in flight when
    // the service's own release for it runs. A mark stays until a new connection with the same id
    // attaches (ForgetTakenOverConnection); ids are remote endpoints, so that is a new socket.
    private readonly ConcurrentQueue<string> _takenOverConnectionQueue = new();
    // Minimum gap between joins from one already-joined connection.
    public static readonly TimeSpan RepeatJoinInterval = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<string, long> _lastRepeatJoin = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _takenOverConnections = new(StringComparer.Ordinal);

    public DedicatedServerHost(
        IAuthoritativeSessionHost? authority,
        IProtocolRouter router)
        : this(authority, router, deferJoinsToTick: false)
    {
    }

    // With deferJoinsToTick, a join request is not applied when it arrives: it waits for the next
    // tick and every join that arrived since the previous tick is applied at the start of that tick
    // in ordinal session-id order. The runtime's join-order state (primary seat, secondary actor
    // ids and spawn lanes) then depends only on which sessions joined in which tick, not on their
    // arrival order. HandleClientMessage returns no reply for a deferred join; the JoinAccepted or
    // JoinRejected comes from DrainPendingJoinRepliesInto after the tick, and the caller must
    // deliver it. DedicatedServerService delivers both kinds of reply; the dedicated and
    // integrated servers turn this on through ServerBootstrap.CreateAuthoritativeHost.
    public DedicatedServerHost(
        IAuthoritativeSessionHost? authority,
        IProtocolRouter router,
        bool deferJoinsToTick)
    {
        _authority = authority;
        _isSessionClaimed = id => _sessionClaims.ContainsKey(id.Value);
        _deferJoinsToTick = deferJoinsToTick;
        _tickDrivenAuthority = authority as ITickDrivenAuthoritativeSessionHost;
        _router = router;
        if (Runtime is { } runtime)
        {
            // Snapshots count the inbox's carried repeat debt in StarvedTicks (W9-2).
            runtime.CarriedRepeatDebtTicks = _commandInbox.GetCarriedRepeatDebtTicks;
            runtime.IsSessionMuted = IsSessionMuted;
        }

        _checkpointWrites =Runtime?.CheckpointWriter is { } checkpointWriter
            ? new BackgroundCheckpointWriter(checkpointWriter, Runtime.PersistenceWrites)
            : null;
    }

    public BackgroundCheckpointWriter? CheckpointWrites => _checkpointWrites;

    public bool DefersJoinsToTick => _deferJoinsToTick;

    // Connections the host still tracks. ReleaseConnection removes one last, under the tick lock,
    // so once this drops the released connection's pending join and seat are gone; a service-level
    // connection count drops before the host's release runs.
    public int ConnectionCount => _connections.Count;

    // How long until the tick runner's next fixed tick is due.
    public double SecondsUntilNextTick =>
        TickPacer.SecondsUntilNextTick(_tickRunner.RemainingSeconds, SimulationClock.FixedDeltaSeconds);

    // Ticks the host's clock skipped after a stall, read from the tick runner itself. Read it on
    // the thread that calls AdvanceTime.
    public long DroppedTicks => _tickRunner.DroppedTicks;

    // Test seam: runs inside ReleaseConnection, under the tick lock, after the released session's
    // claim is confirmed and before its queued actions are purged.
    internal Action<string>? ReleasingSessionForTest { get; set; }

    // Clock for connection liveness (stale-session takeover). DedicatedServerService sets it to its
    // connection options' clock; tests pass a fake one.
    internal TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    // The TimeProvider timestamp of the latest ObserveInboundFrames call, the service's per-tick
    // liveness sweep; 0 until the first one. Keepalive frames only reach a context through that
    // sweep, so once a sweep has run, silence is measured up to it rather than up to now: a tick
    // stall delays the sweep, and a join routed right after the stall must not see every idle
    // client as silent for the stall's length. A host with no sweep (no service) measures to now.
    private long _lastInboundSweepTimestamp;

    // Join throttle (ServerRoutingContext.JoinThrottleInitialInterval): per source address and
    // contested session id or player name, touched only under _authoritySync.
    private readonly JoinThrottleTable _joinThrottle = new();

    // Chat token buckets per account, so a reconnect keeps its bucket (bounded, outlives ReleaseConnection).
    private readonly ChatRateLimiter _chatLimiter = new();

    // Per-source limit on new accounts (NewAccountLimiter), touched only under _authoritySync.
    internal NewAccountLimiter NewAccountLimits { get; set; } = NewAccountLimiter.FromEnvironment();

    public string? CheckNewAccountLimit(string connectionId)
    {
        lock (_authoritySync)
        {
            int seconds = NewAccountLimits.SecondsUntilAllowed(
                JoinThrottleTable.ResolveSource(connectionId),
                TimeProvider.GetTimestamp(),
                TimeProvider.TimestampFrequency);
            return seconds == 0 ? null : $"{NewAccountRejectionReason} Try again in {seconds} s.";
        }
    }

    public void RecordNewAccount(string connectionId)
    {
        lock (_authoritySync)
        {
            NewAccountLimits.Record(
                JoinThrottleTable.ResolveSource(connectionId),
                TimeProvider.GetTimestamp(),
                TimeProvider.TimestampFrequency);
        }
    }

    // Joins the join throttle refused (dropped, or answered with JoinThrottledRejectionReason).
    private long _throttledJoinAttempts;

    internal long ThrottledJoinAttempts => Interlocked.Read(ref _throttledJoinAttempts);

    internal int JoinThrottleEntryCount
    {
        get
        {
            lock (_authoritySync)
            {
                return _joinThrottle.Count;
            }
        }
    }

    // Whether a join for this session id and display name from this connection's source would be
    // throttled now.
    internal bool IsJoinThrottled(string connectionId, string sessionId, string? displayName, AccountKey? account = null)
    {
        lock (_authoritySync)
        {
            return FindJoinThrottle(connectionId, sessionId, displayName, account, TimeProvider.GetTimestamp(), out _);
        }
    }

    // The integrated server turns the development commands on; the dedicated server leaves them to
    // DREAM_DEV_COMMANDS (ServerBootstrap).
    public bool DevCommandsAllowed
    {
        get => _authority?.DevCommandsAllowed ?? false;
        set
        {
            if (_authority is RuntimeAuthoritativeSessionHost runtimeAuthority)
            {
                runtimeAuthority.DevCommandsAllowed = value;
            }
        }
    }

    public WorldSessionRuntime? Runtime => (_authority as RuntimeAuthoritativeSessionHost)?.Runtime;

    // M1-C: queues the answer to a character-list read as a reply for the connection (sent after the next tick).
    internal void EnqueueConnectionReply(string connectionId, IServerToClientMessage message) =>
        _pendingConnectionReplies.Enqueue(new PendingConnectionReply(connectionId, message));

    public static DedicatedServerHost CreateDefault()
    {
        ProtocolRouter router = new(
            [
                new ClientHelloHandler(),
                new CharacterListHandler(),
                new JoinRequestHandler(),
                new PlayerCommandHandler(),
                new PlayerActionHandler(),
                new WorldResyncRequestHandler(),
                new ChatMessageHandler()
            ],
            Array.Empty<IClientMessageHandler>());

        return new DedicatedServerHost(authority: null, router);
    }

    public static DedicatedServerHost CreateRuntimeAttached(
        Dream.Server.Persistence.ServerPersistenceBundle? persistence = null,
        bool deferJoinsToTick = false)
    {
        CharacterListHandler characterLists = new();
        ProtocolRouter router = new(
            [
                new ClientHelloHandler(),
                characterLists,
                new JoinRequestHandler(),
                new PlayerCommandHandler(),
                new PlayerActionHandler(),
                new WorldResyncRequestHandler(),
                new ChatMessageHandler()
            ],
            Array.Empty<IClientMessageHandler>());

        RuntimeAuthoritativeSessionHost runtimeHost = ServerBootstrap.CreateRuntimeHost(persistence);
        try
        {
            DedicatedServerHost host = new(runtimeHost, router, deferJoinsToTick);
            characterLists.Attach(persistence?.Accounts, runtimeHost.Runtime.PersistenceWrites);
            characterLists.Deliver = host.EnqueueConnectionReply;
            return host;
        }
        catch
        {
            runtimeHost.Dispose();
            throw;
        }
    }

    public IReadOnlyList<IServerToClientMessage> HandleClientMessage(
        string connectionId,
        IClientToServerMessage message)
    {
        if (Volatile.Read(ref _disposed) || _takenOverConnections.ContainsKey(connectionId))
        {
            return Array.Empty<IServerToClientMessage>();
        }

        ServerRoutingContext context = GetOrCreateConnectionContext(connectionId);
        lock (_authoritySync)
        {
            // A connection whose session was taken over while this message was on its way: the
            // context fetched above is a fresh one created after the takeover removed the old one,
            // so it is dropped again instead of lingering as a ghost.
            if (_takenOverConnections.ContainsKey(connectionId))
            {
                if (_connections.TryRemove(new KeyValuePair<string, ServerRoutingContext>(connectionId, context)))
                {
                    Volatile.Write(ref _connectionsChanged, 1);
                }

                return Array.Empty<IServerToClientMessage>();
            }

            // The context was fetched before the lock. If the connection was released (or the host
            // disposed) in between, the message belongs to a connection that no longer exists: a
            // command routed now would leave a stale ring in the inbox for a session a reconnect
            // may already have claimed.
            if (_disposed
                || !_connections.TryGetValue(connectionId, out ServerRoutingContext? current)
                || !ReferenceEquals(current, context))
            {
                return Array.Empty<IServerToClientMessage>();
            }

            context.MarkInbound(TimeProvider.GetTimestamp());

            if (message is ClientJoinRequestMessage rawJoinRequest)
            {
                // A joined connection may repeat a join (a name change is refused, a same-seat repeat
                // re-exports the world) at most once per RepeatJoinInterval; extras are dropped unanswered.
                if (context.LastJoinRequest != null)
                {
                    long joinNow = TimeProvider.GetTimestamp();
                    if (_lastRepeatJoin.TryGetValue(connectionId, out long lastRepeat)
                        && joinNow - lastRepeat < (long)(RepeatJoinInterval.TotalSeconds * TimeProvider.TimestampFrequency))
                    {
                        return Array.Empty<IServerToClientMessage>();
                    }

                    _lastRepeatJoin[connectionId] = joinNow;
                }

                // On a host with accounts the token is checked and replaced by its hash before anything
                // throttles, queues, routes or stores the message, so no later code (PendingJoin,
                // LastJoinRequest, logs) can see it. A bad or missing token starts no prefetch.
                ClientJoinRequestMessage joinRequest = rawJoinRequest;
                if (Runtime is { HasAccounts: true })
                {
                    // The server's own seat is not joinable from the network: the local branch of the join
                    // skips character binding, restore and the access commit.
                    if (string.Equals(rawJoinRequest.RequestedSessionId, PlayerSessionId.LocalDefault.Value, StringComparison.Ordinal))
                    {
                        return RejectJoinBeforeQueue(context, SessionInUseRejectionReason);
                    }

                    if (!AccountKey.TryFromToken(rawJoinRequest.AccountToken, out AccountKey account))
                    {
                        return RejectJoinBeforeQueue(
                            context,
                            rawJoinRequest.AccountToken == null
                                ? JoinRejectionReasons.TokenRequired
                                : JoinRejectionReasons.TokenInvalid);
                    }

                    if (string.IsNullOrWhiteSpace(rawJoinRequest.DisplayName))
                    {
                        return RejectJoinBeforeQueue(context, JoinRejectionReasons.NameInvalid);
                    }

                    joinRequest = rawJoinRequest with { AccountToken = null, ResolvedAccount = account };

                    // A ban set here moments ago may not be in the join's persisted-state load yet.
                    if (RecentBanText(account) is { } recentBan)
                    {
                        return RejectJoinBeforeQueue(context, recentBan);
                    }
                }
                else if (rawJoinRequest.AccountToken != null || rawJoinRequest.ResolvedAccount != null)
                {
                    // No accounts here: the token has no use, and is not kept.
                    joinRequest = rawJoinRequest with { AccountToken = null, ResolvedAccount = null };
                }

                message = joinRequest;

                // The player cap: a connection with no seat is refused while every seat is taken. A joined
                // connection repeating its join keeps its seat.
                if (IsServerFullFor(connectionId, joinRequest))
                {
                    return RejectJoinBeforeQueue(context, ServerConnectionOptions.ServerFullRejectionReason);
                }

                if (TryThrottleJoin(context, joinRequest, out IServerToClientMessage? throttleReply))
                {
                    return throttleReply == null ? Array.Empty<IServerToClientMessage>() : [throttleReply];
                }

                if (_deferJoinsToTick)
                {
                    QueueJoin(connectionId, joinRequest);
                    return Array.Empty<IServerToClientMessage>();
                }
            }

            context.ClearOutboundMessages();
            context.ClearJoinConflict();
            context.Clock = TimeProvider;
            context.ChatLimiter = _chatLimiter;
            _router.RouteServerInbound(context, message);
            if (context.PendingChats.Count > 0)
            {
                DeliverPendingChats(context);
            }

            ServerChatMessage? motd = null;
            if (message is ClientJoinRequestMessage routedJoin)
            {
                CompleteJoinAttempt(context, routedJoin);
                if (_connectionClaims.ContainsKey(context.ConnectionId))
                {
                    EnqueueGroundReset(context.ConnectionId, Runtime);
                    if (HasJoinAccepted(context.OutboundMessages))
                    {
                        motd = CompleteAcceptedJoin(context);
                    }
                }
            }

            return motd == null
                ? context.OutboundMessages.ToArray()
                : [.. context.OutboundMessages, motd];
        }
    }

    // Hands the lines ChatMessageHandler queued on the context to ChatRouter and queues the results
    // as pending connection replies, so the simulation never sees chat. Under the tick lock.
    private void DeliverPendingChats(ServerRoutingContext context)
    {
        List<ClientChatMessage> lines = [.. context.PendingChats];
        context.PendingChats.Clear();
        if (Runtime is not { } runtime
            || !_connectionClaims.TryGetValue(context.ConnectionId, out string? senderSession))
        {
            return;
        }

        List<ChatParticipant> participants = [];
        ChatParticipant? sender = null;
        foreach (ActorSnapshot actor in runtime.CaptureActorSnapshots())
        {
            if (actor.Kind != CreatureKind.Player
                || actor.SessionId == null
                || !_sessionClaims.TryGetValue(actor.SessionId, out string? connectionId))
            {
                continue;
            }

            ChatParticipant participant = new(connectionId, actor.DisplayName ?? actor.SessionId, actor.Position);
            participants.Add(participant);
            if (string.Equals(actor.SessionId, senderSession, StringComparison.Ordinal))
            {
                sender = participant;
            }
        }

        if (sender is not { } found)
        {
            return;
        }

        List<PendingConnectionReply> replies = [];
        for (int i = 0; i < lines.Count; i++)
        {
            // M8: slash commands are decided by the server alone, and a muted account is not heard.
            if (lines[i].Channel == ChatChannel.Command)
            {
                HandleAdminCommand(context, found, lines[i], replies);
                continue;
            }

            if (IsMuted(context))
            {
                replies.Add(new PendingConnectionReply(
                    found.ConnectionId,
                    new ServerChatMessage(ChatChannel.System, "", MutedText, runtime.CurrentServerTick)));
                continue;
            }

            // M5: the party chat goes to the sender's party members only.
            List<ChatParticipant>? party = lines[i].Channel == ChatChannel.Party
                ? BuildPartyParticipants(runtime, senderSession, participants)
                : null;
            ChatRouter.Route(found, lines[i], participants, runtime.CurrentServerTick, replies, party);
        }

        for (int i = 0; i < replies.Count; i++)
        {
            _pendingConnectionReplies.Enqueue(replies[i]);
        }
    }

    // The chat participants that share the sender's party (the sender included); null when the sender is
    // in none.
    private List<ChatParticipant>? BuildPartyParticipants(
        WorldSessionRuntime runtime,
        string senderSession,
        List<ChatParticipant> participants)
    {
        List<PlayerSessionId> members = [];
        if (!runtime.CopyPartyMemberSessions(new PlayerSessionId(senderSession), members))
        {
            return null;
        }

        List<ChatParticipant> party = [];
        for (int m = 0; m < members.Count; m++)
        {
            if (!_sessionClaims.TryGetValue(members[m].Value, out string? connectionId))
            {
                continue;
            }

            for (int p = 0; p < participants.Count; p++)
            {
                if (string.Equals(participants[p].ConnectionId, connectionId, StringComparison.Ordinal))
                {
                    party.Add(participants[p]);
                    break;
                }
            }
        }

        return party;
    }

    // A join refused before it is throttled, queued or prefetched (missing or malformed token, blank
    // name on a host with accounts). The connection stays open and unclaimed. Under the tick lock.
    private IReadOnlyList<IServerToClientMessage> RejectJoinBeforeQueue(ServerRoutingContext context, string reason)
    {
        context.JoinRejectionSent = true;
        NetworkDiagnosticsLog.Write(
            "server.join.rejected",
            $"connection={context.ConnectionId} reason={(reason == JoinRejectionReasons.NameInvalid ? "name-invalid" : reason == JoinRejectionReasons.TokenRequired ? "token-required" : reason == ServerConnectionOptions.ServerFullRejectionReason ? "server-full" : reason.StartsWith(JoinRejectionReasons.Banned, StringComparison.Ordinal) ? "banned" : reason == JoinRejectionReasons.TokenInvalid ? "token-invalid" : "refused")}");
        return [new JoinRejectedMessage(reason)];
    }

    // Queues an action for a session from the server side (a scripted bot, an admin tool or a
    // test), bypassing the player.action allow-list that keeps clients from sending build and
    // combat use directly. The next tick drains it once the session is joined and validates it
    // like a command-synthesized action. False when the session's pending-action cap is full.
    internal bool TryEnqueueServerAction(PlayerActionMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _actionQueue.TryEnqueue(message, out _);
    }

    public bool TryGetConnectionContext(string connectionId, out ServerRoutingContext? context)
    {
        return _connections.TryGetValue(connectionId, out context);
    }

    public bool IsConnectionJoined(string connectionId)
    {
        return _connections.TryGetValue(connectionId, out ServerRoutingContext? context)
            && context.LastJoinRequest != null;
    }

    // Server-initiated world resync (a lagging connection's merged deltas overflowed). Unlike
    // HandleClientMessage it never creates a connection context: a connection the host no longer
    // tracks (already released) is skipped instead of coming back as a ghost. The request goes
    // through WorldResyncRequestHandler like a client's, so the same cooldown applies. True when a
    // resync is now pending for the connection; false when the connection is unknown or not
    // joined, or the cooldown refused it (the caller may retry later).
    // Shared by every overflow resync request: the record is immutable, and the service re-asks
    // every tick while the cooldown refuses, so this path must not allocate.
    private static readonly ClientWorldResyncRequestMessage OverflowResyncRequest = new(ClientChecksum: 0);

    public bool RequestWorldResyncIfConnected(string connectionId)
    {
        lock (_authoritySync)
        {
            if (_disposed || !_connections.TryGetValue(connectionId, out ServerRoutingContext? context))
            {
                return false;
            }

            context.ClearOutboundMessages();
            _router.RouteServerInbound(context, OverflowResyncRequest);
            context.ClearOutboundMessages();
            return context.WorldResyncPending;
        }
    }

    public int AdvanceTime(double elapsedSeconds)
    {
        if (_tickDrivenAuthority == null)
        {
            throw new InvalidOperationException(
                "Cannot advance server time before a runtime-backed authoritative session host is attached.");
        }

        lock (_authoritySync)
        {
            if (_disposed)
            {
                return 0;
            }

            return _tickRunner.Advance(
                elapsedSeconds,
                this,
                static (host, tick, fixedDeltaSeconds) => host.RunTick(tick, fixedDeltaSeconds));
        }
    }

    public IReadOnlyList<AuthoritativeActionResult> DrainPendingActionResults()
    {
        List<AuthoritativeActionResult> drained = [];
        DrainPendingActionResultsInto(drained);
        return drained;
    }

    // Replaces the destination's contents with every pending action result, oldest first.
    public void DrainPendingActionResultsInto(List<AuthoritativeActionResult> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        destination.Clear();
        // Under the tick lock, like the purge in ReleaseConnection, so a purge never reorders what a drain takes.
        lock (_authoritySync)
        {
            while (_pendingActionResults.TryDequeue(out AuthoritativeActionResult actionResult))
            {
                destination.Add(actionResult);
            }
        }
    }

    // Drops the queued results of a departed session, keeping the rest in order. A result carries the
    // inventory of its own tick and is delivered by session id, so one left queued across a leave and a
    // rejoin of the same id would reach the new connection after its JoinAccepted. Caller holds _authoritySync.
    private void PurgePendingActionResults(string sessionId)
    {
        int count = _pendingActionResults.Count;
        for (int i = 0; i < count && _pendingActionResults.TryDequeue(out AuthoritativeActionResult result); i++)
        {
            if (!string.Equals(result.SessionId, sessionId, StringComparison.Ordinal))
            {
                _pendingActionResults.Enqueue(result);
            }
        }
    }

    // Replaces the destination's contents with the replies produced during ticks, oldest first:
    // replies to joins applied at tick boundaries (only when the host defers joins to the tick) and
    // world resyncs served at the end of a tick. DedicatedServerService delivers them after
    // AdvanceTime, before the tick's snapshot.
    public void DrainPendingJoinRepliesInto(List<PendingConnectionReply> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        destination.Clear();
        while (_pendingConnectionReplies.TryDequeue(out PendingConnectionReply reply))
        {
            destination.Add(reply);
        }
    }

    public ServerSnapshotMessage CreateSnapshotMessage(bool includeWorldSnapshot = false)
    {
        if (_authority == null)
        {
            throw new InvalidOperationException(
                "Cannot export a server snapshot before an authoritative session host is attached.");
        }

        lock (_authoritySync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new ServerSnapshotMessage(_authority.ExportSnapshot(includeWorldSnapshot));
        }
    }

    // With no joined receiver, pending world and damage deltas have nobody to reach. Later joiners
    // get the current world in JoinAccepted, so the backlog is dropped instead of replayed on top of it.
    public void DiscardUndeliveredSnapshotDeltas()
    {
        if (_authority == null)
        {
            return;
        }

        lock (_authoritySync)
        {
            if (!_disposed)
            {
                _ = _authority.ExportSnapshot();
            }
        }
    }

    public bool TryClaimSession(string connectionId, string sessionId, out string? rejectionReason)
    {
        return TryClaimSession(connectionId, sessionId, displayName: null, out rejectionReason);
    }

    // Claims run under the tick lock (joins are routed under it; the lock is re-entrant), so a
    // takeover's release cannot interleave with a tick, another join or the old connection's own
    // release. Identity is unauthenticated: a session id is a client-chosen string (a fresh
    // "client-<guid>" per connect, broadcast to every client in snapshots) and the persisted
    // profile is keyed by the display name. So a rejoin after a client crash normally arrives with
    // a new session id and the same display name, and a stale holder is looked up both ways:
    //  - the same session id claimed by another connection;
    //  - another claimed connection whose joined player name (ResolveJoinPlayerName) matches.
    // Either is taken over only when it has been silent for TakeoverSilence; anyone could already take the name once the old connection's
    // 10 s idle timeout released it, so the takeover only shortens that window for a dead
    // connection and never touches a live one. A live holder of the same session id rejects the
    // join with SessionInUseRejectionReason; a live holder of the name is left to the runtime's
    // duplicate-display-name rejection. Either marks the joining context with the contested
    // session id or name, so a rejected join throttles later joins for it from the same source
    // address (JoinThrottleTable).
    public bool TryClaimSession(string connectionId, string sessionId, string? displayName, out string? rejectionReason)
        => TryClaimSession(connectionId, sessionId, displayName, holder: null, out rejectionReason);

    // holder: the character and account the join was authorised for (JoinRequestHandler), or null on a
    // host without accounts. With a holder, who may take whose seat is decided by account, never by
    // silence (see TryClaimSessionForHolderLocked); without one, the name and silence rules above apply.
    public bool TryClaimSession(
        string connectionId,
        string sessionId,
        string? displayName,
        JoinHolderIdentity? holder,
        out string? rejectionReason)
    {
        lock (_authoritySync)
        {
            return TryClaimSessionLocked(connectionId, sessionId, displayName, holder, out rejectionReason);
        }
    }

    private bool TryClaimSessionLocked(
        string connectionId,
        string sessionId,
        string? displayName,
        JoinHolderIdentity? holder,
        out string? rejectionReason)
    {
        if (_connectionClaims.TryGetValue(connectionId, out string? existingSession))
        {
            if (string.Equals(existingSession, sessionId, StringComparison.Ordinal))
            {
                rejectionReason = null;
                return true;
            }

            rejectionReason = $"Connection is already joined as session '{existingSession}'.";
            return false;
        }

        if (holder is { } identity && !TryResolveHolderConflictsLocked(connectionId, sessionId, displayName, identity, out rejectionReason))
        {
            return false;
        }

        if (holder == null
            && _sessionClaims.TryGetValue(sessionId, out string? currentOwner)
            && !string.Equals(currentOwner, connectionId, StringComparison.Ordinal)
            && !TryTakeOverStaleConnection(currentOwner, connectionId, sessionId, "session-id", TakeoverSilence))
        {
            MarkSessionConflictWithLiveHolder(connectionId, sessionId);
            NetworkDiagnosticsLog.Write(
                "server.session.in-use",
                $"connection={connectionId} session={sessionId} owner={currentOwner}");
            rejectionReason = SessionInUseRejectionReason;
            return false;
        }

        if (holder == null && displayName != null)
        {
            TakeOverStaleHoldersOfPlayerName(connectionId, sessionId, displayName);
        }

        if (_sessionClaims.TryAdd(sessionId, connectionId))
        {
            _connectionClaims[connectionId] = sessionId;
            // A fresh claim starts the session's command stream over (the client counts from 1
            // again). The release already removed the ring, but a command routed from the departed
            // connection's context could have recreated it with that connection's high sequence,
            // which would drop the new connection's frames as stale.
            _commandInbox.Remove(new PlayerSessionId(sessionId));
            rejectionReason = null;
            return true;
        }

        if (_sessionClaims.TryGetValue(sessionId, out string? ownerConnection)
            && string.Equals(ownerConnection, connectionId, StringComparison.Ordinal))
        {
            _connectionClaims[connectionId] = sessionId;
            rejectionReason = null;
            return true;
        }

        MarkSessionConflictWithLiveHolder(connectionId, sessionId);
        rejectionReason = SessionInUseRejectionReason;
        return false;
    }

    // Seats of a join that has an identity (a host with accounts). Ownership was already decided
    // (JoinRequestHandler runs AuthorizeJoin before this), so here the account decides who may take what:
    //  - a session id or character held by the SAME account is taken over at once, live or silent: the
    //    owner's own new connection replaces its old one (a crash rejoin no longer waits for the window);
    //  - a session id held by another account is never taken over, stale or not (the 10 s idle release
    //    frees it) and gets SessionInUseRejectionReason;
    //  - a character held by another account (it was unowned when the holder joined, then released)
    //    gets CharacterInUse.
    // Under the tick lock.
    private bool TryResolveHolderConflictsLocked(
        string connectionId,
        string sessionId,
        string? displayName,
        JoinHolderIdentity holder,
        out string? rejectionReason)
    {
        rejectionReason = null;
        if (_sessionClaims.TryGetValue(sessionId, out string? sessionOwner)
            && !string.Equals(sessionOwner, connectionId, StringComparison.Ordinal))
        {
            // A claim without a context is a leftover (see TryTakeOverStaleConnection).
            bool sameAccount = !_connections.TryGetValue(sessionOwner, out ServerRoutingContext? ownerContext)
                || (ownerContext.JoinedIdentity is { } ownerIdentity && ownerIdentity.Account.Equals(holder.Account));
            if (!sameAccount
                || !TryTakeOverStaleConnection(sessionOwner, connectionId, sessionId, "same-account", TakeoverSilence, force: true))
            {
                MarkSessionConflictWithLiveHolder(connectionId, sessionId);
                NetworkDiagnosticsLog.Write(
                    "server.session.in-use",
                    $"connection={connectionId} session={sessionId} owner={sessionOwner}");
                rejectionReason = SessionInUseRejectionReason;
                return false;
            }
        }

        List<string>? sameAccountHolders = null;
        foreach (KeyValuePair<string, string> claim in _connectionClaims)
        {
            if (string.Equals(claim.Key, connectionId, StringComparison.Ordinal)
                || !_connections.TryGetValue(claim.Key, out ServerRoutingContext? other)
                || other.JoinedIdentity is not { } otherIdentity
                || !otherIdentity.Character.Equals(holder.Character))
            {
                continue;
            }

            if (!otherIdentity.Account.Equals(holder.Account))
            {
                MarkPlayerNameConflictWithLiveHolder(connectionId, displayName ?? holder.Character.Value);
                rejectionReason = JoinRejectionReasons.CharacterInUse;
                return false;
            }

            (sameAccountHolders ??= []).Add(claim.Key);
        }

        // Released outside the enumeration, which the release edits.
        if (sameAccountHolders != null)
        {
            foreach (string staleConnection in sameAccountHolders)
            {
                TryTakeOverStaleConnection(staleConnection, connectionId, sessionId, "same-account-character", TakeoverSilence, force: true);
            }
        }

        return true;
    }

    private void MarkSessionConflictWithLiveHolder(string connectionId, string sessionId)
    {
        if (_connections.TryGetValue(connectionId, out ServerRoutingContext? context))
        {
            context.JoinConflictSessionId = sessionId;
        }
    }

    private void MarkPlayerNameConflictWithLiveHolder(string connectionId, string playerName)
    {
        if (_connections.TryGetValue(connectionId, out ServerRoutingContext? context))
        {
            context.JoinConflictPlayerName = playerName;
        }
    }

    // Join throttle (JoinThrottleTable): refuses a join whose session id or player name a live
    // holder rejected for an earlier join from the same source address, inside the throttle
    // interval. The join is not claimed, queued or prefetched. A connection already answered with
    // a rejection gets nothing back (reply is null); a fresh one gets one JoinRejected with
    // JoinThrottledRejectionReason. The first refusal of each interval is logged with the time
    // left; later ones only count. Under the tick lock.
    private bool TryThrottleJoin(ServerRoutingContext context, ClientJoinRequestMessage join, out IServerToClientMessage? reply)
    {
        reply = null;
        long now = TimeProvider.GetTimestamp();
        if (!FindJoinThrottle(context.ConnectionId, join.RequestedSessionId, join.DisplayName, join.ResolvedAccount, now, out JoinThrottleTable.Key key))
        {
            return false;
        }

        Interlocked.Increment(ref _throttledJoinAttempts);
        int secondsLeft = _joinThrottle.SecondsLeft(key, now, TimeProvider.TimestampFrequency);
        if (_joinThrottle.RecordDroppedJoinAttempt(key) == 1)
        {
            NetworkDiagnosticsLog.Write(
                "server.join.throttled",
                $"connection={context.ConnectionId} source={key.Source} session={join.RequestedSessionId} name={join.DisplayName} " +
                $"contested={(key.IsSession ? "session-id" : "player-name")} secondsLeft={secondsLeft} reason=live-holder-rejected-earlier-join");
        }

        if (!context.JoinRejectionSent)
        {
            context.JoinRejectionSent = true;
            reply = new JoinRejectedMessage(
                $"{JoinThrottledRejectionReason} Retry in {Math.Max(1, secondsLeft)} s.");
        }

        return true;
    }

    // Whether the join's session id, or its resolved player name, is throttled for the
    // connection's source; key is the throttled one. Under the tick lock.
    private bool FindJoinThrottle(string connectionId, string sessionId, string? displayName, AccountKey? account, long now, out JoinThrottleTable.Key key)
    {
        string source = JoinThrottleTable.ResolveSource(connectionId);
        long frequency = TimeProvider.TimestampFrequency;
        key = JoinThrottleTable.SessionKey(source, sessionId, account?.HashHex);
        if (_joinThrottle.IsThrottled(key, now, frequency))
        {
            return true;
        }

        key = JoinThrottleTable.PlayerNameKey(source, WorldSessionRuntime.ResolveJoinPlayerName(sessionId, displayName), account?.HashHex);
        return _joinThrottle.IsThrottled(key, now, frequency);
    }

    // After a routed join: a join a live holder rejected (the connection is still unclaimed)
    // starts or extends the throttle on its contested session id and/or player name for the
    // connection's source; an accepted one clears the source's throttle on its session id and
    // name. Under the tick lock.
    private void CompleteJoinAttempt(ServerRoutingContext context, ClientJoinRequestMessage join)
    {
        string? conflictSession = context.JoinConflictSessionId;
        string? conflictName = context.JoinConflictPlayerName;
        context.ClearJoinConflict();
        string source = JoinThrottleTable.ResolveSource(context.ConnectionId);
        if (_connectionClaims.ContainsKey(context.ConnectionId))
        {
            context.JoinRejectionSent = false;
            _joinThrottle.Reset(JoinThrottleTable.SessionKey(source, join.RequestedSessionId, join.ResolvedAccount?.HashHex));
            _joinThrottle.Reset(JoinThrottleTable.PlayerNameKey(
                source,
                WorldSessionRuntime.ResolveJoinPlayerName(join.RequestedSessionId, join.DisplayName),
                join.ResolvedAccount?.HashHex));
            return;
        }

        if ((conflictSession == null && conflictName == null) || _disposed)
        {
            return;
        }

        // A name conflict is rejected by the runtime, after which JoinRequestHandler releases the
        // connection, context included. The connection is still open (it just sent this join), so
        // its context goes back, unclaimed, to remember that it was answered; its next message
        // would have created a fresh one anyway, and its close releases this one like any other.
        if (_connections.TryAdd(context.ConnectionId, context))
        {
            Volatile.Write(ref _connectionsChanged, 1);
        }

        context.JoinRejectionSent = true;
        if (conflictSession != null)
        {
            ThrottleContestedJoin(context, join, JoinThrottleTable.SessionKey(source, conflictSession, join.ResolvedAccount?.HashHex));
        }

        if (conflictName != null)
        {
            ThrottleContestedJoin(context, join, JoinThrottleTable.PlayerNameKey(source, conflictName, join.ResolvedAccount?.HashHex));
        }
    }

    private void ThrottleContestedJoin(ServerRoutingContext context, ClientJoinRequestMessage join, JoinThrottleTable.Key key)
    {
        long intervalTicks = _joinThrottle.Throttle(key, TimeProvider.GetTimestamp(), TimeProvider.TimestampFrequency, out int droppedBefore);
        NetworkDiagnosticsLog.Write(
            "server.join.throttle",
            $"connection={context.ConnectionId} source={key.Source} session={join.RequestedSessionId} name={join.DisplayName} " +
            $"contested={(key.IsSession ? "session-id" : "player-name")} " +
            $"intervalMs={intervalTicks * 1000d / TimeProvider.TimestampFrequency:0} droppedLastInterval={droppedBefore}");
    }

    // Whether a join of another connection, still pending or in the batch being applied, waits on
    // the same player's prefetch. Under the tick lock.
    private bool IsPlayerPrefetchSharedByAnotherJoin(string playerName, AccountKey? account, string releasedConnectionId)
    {
        return ContainsJoinOfPlayer(_pendingJoins, playerName, account, releasedConnectionId)
            || ContainsJoinOfPlayer(_applyingJoins, playerName, account, releasedConnectionId);
    }

    private static bool ContainsJoinOfPlayer(List<PendingJoin> joins, string playerName, AccountKey? account, string excludedConnectionId)
    {
        for (int i = 0; i < joins.Count; i++)
        {
            if (!string.Equals(joins[i].ConnectionId, excludedConnectionId, StringComparison.Ordinal)
                && joins[i].Request.ResolvedAccount.Equals(account)
                && CharacterNames.Comparer.Equals(joins[i].PlayerName, playerName))
            {
                return true;
            }
        }

        return false;
    }

    // Takes over every other claimed connection whose joined player is the joining one's player
    // (the same ResolveJoinPlayerName, compared like the runtime's display-name check) under a
    // different session id, when it is stale. A live one is left alone (the runtime then rejects
    // the duplicate name) and marks the joining context. Under the tick lock.
    private void TakeOverStaleHoldersOfPlayerName(string connectionId, string sessionId, string displayName)
    {
        string playerName = WorldSessionRuntime.ResolveJoinPlayerName(sessionId, displayName);
        string? staleHolder = null;
        do
        {
            staleHolder = null;
            foreach (KeyValuePair<string, string> claim in _connectionClaims)
            {
                if (string.Equals(claim.Key, connectionId, StringComparison.Ordinal)
                    || string.Equals(claim.Value, sessionId, StringComparison.Ordinal)
                    || !_connections.TryGetValue(claim.Key, out ServerRoutingContext? holder)
                    || holder.LastJoinRequest is not { } holderJoin
                    || !CharacterNames.Comparer.Equals(
                        WorldSessionRuntime.ResolveJoinPlayerName(holderJoin.RequestedSessionId, holderJoin.DisplayName),
                        playerName))
                {
                    continue;
                }

                if (!IsConnectionStale(holder, TakeoverSilence))
                {
                    MarkPlayerNameConflictWithLiveHolder(connectionId, playerName);
                    continue;
                }

                staleHolder = claim.Key;
                break;
            }

            // Released outside the enumeration, which the release edits.
            if (staleHolder != null
                && !TryTakeOverStaleConnection(staleHolder, connectionId, sessionId, "player-name", TakeoverSilence))
            {
                MarkPlayerNameConflictWithLiveHolder(connectionId, playerName);
                return;
            }
        }
        while (staleHolder != null);
    }

    private bool IsConnectionStale(ServerRoutingContext context, TimeSpan window)
    {
        return GetSilenceTicks(context) >= (long)(window.TotalSeconds * TimeProvider.TimestampFrequency);
    }

    // How long the connection is known to have been silent: from its last inbound frame up to the
    // latest liveness sweep (see _lastInboundSweepTimestamp), or up to now when no sweep has run.
    // A routed message marks the context with the current time, which can be past the sweep; that
    // is negative silence, i.e. live.
    private long GetSilenceTicks(ServerRoutingContext context)
    {
        long sweep = Volatile.Read(ref _lastInboundSweepTimestamp);
        long now = TimeProvider.GetTimestamp();
        long measuredTo = sweep != 0 && sweep < now ? sweep : now;
        return measuredTo - context.LastInboundTimestamp;
    }

    // Releases a stale connection's claim, queued actions, command ring and runtime seat through
    // ReleaseConnection, so the joining connection can claim the session (or the player) next.
    // The release saves the departing player's profile into the runtime's profile cache, which
    // RestorePersistedPlayerState prefers over the join's older prefetch, and keeps any prefetch
    // a pending join still needs (see ReleaseConnection). False when the holder is still live.
    // Under the tick lock.
    private bool TryTakeOverStaleConnection(
        string staleConnectionId,
        string takingConnectionId,
        string sessionId,
        string match,
        TimeSpan window,
        bool force = false)
    {
        if (_disposed)
        {
            return false;
        }

        // A claim always has a context (both go in ReleaseConnection); one without is a leftover,
        // and taking it over is safe. force: the same account's own connection, taken over live or silent.
        if (_connections.TryGetValue(staleConnectionId, out ServerRoutingContext? staleContext)
            && !force
            && !IsConnectionStale(staleContext, window))
        {
            return false;
        }

        double silenceSeconds = staleContext == null
            ? double.PositiveInfinity
            : GetSilenceTicks(staleContext) / (double)TimeProvider.TimestampFrequency;
        NetworkDiagnosticsLog.Write(
            "server.session.takeover",
            $"connection={takingConnectionId} session={sessionId} stale={staleConnectionId} match={match} silence={silenceSeconds:0.###}s");
        _takenOverConnections[staleConnectionId] = 0;
        ReleaseConnection(staleConnectionId);
        _takenOverConnectionQueue.Enqueue(staleConnectionId);
        return true;
    }

    // The next connection whose session a join took over. The host already released it; the
    // caller (DedicatedServerService) closes its transport; a later ReleaseConnection for it finds
    // nothing left. Allocation-free when there is none.
    public bool TryTakeTakenOverConnection(out string connectionId)
    {
        return _takenOverConnectionQueue.TryDequeue(out connectionId!);
    }

    // A new connection with this id (a remote endpoint reused by a new socket) is attached, so
    // messages under the id are no longer the taken-over connection's late frames.
    public void ForgetTakenOverConnection(string connectionId)
    {
        _takenOverConnections.TryRemove(connectionId, out _);
    }

    // Liveness from the transport: framesReceived is the connection's total of complete inbound
    // frames, empty keepalive frames included, which the host never sees as messages. Called by
    // the service on its tick thread for every connection; allocation-free.
    // Each call also stamps the host's latest sweep (_lastInboundSweepTimestamp).
    public void ObserveInboundFrames(string connectionId, long framesReceived)
    {
        long now = TimeProvider.GetTimestamp();
        if (_connections.TryGetValue(connectionId, out ServerRoutingContext? context))
        {
            context.ObserveInboundFrameCount(framesReceived, now);
        }

        Volatile.Write(ref _lastInboundSweepTimestamp, now);
    }

    // Releasing a connection runs entirely under the tick lock, where every claim is taken (joins
    // are routed under it, deferred or not). The departed session's queued actions are purged and
    // its runtime seat released before its claim is dropped, so a reconnect that reuses the
    // session id cannot claim it in between and then lose its fresh actions or its new seat.
    //
    // After Dispose only the host's own bookkeeping is dropped: the runtime (and its Bullet world)
    // is gone, and a late release from a connection loop must not touch it.
    public void ReleaseConnection(string connectionId)
    {
        lock (_authoritySync)
        {
            _lastRepeatJoin.TryRemove(connectionId, out _);
            if (_disposed)
            {
                _pendingJoins.RemoveAll(join => string.Equals(join.ConnectionId, connectionId, StringComparison.Ordinal));
                if (_connectionClaims.TryRemove(connectionId, out string? releasedSession))
                {
                    _sessionClaims.TryRemove(new KeyValuePair<string, string>(releasedSession, connectionId));
                }

                _connections.TryRemove(connectionId, out _);
                return;
            }

            if (_deferJoinsToTick)
            {
                for (int i = _pendingJoins.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(_pendingJoins[i].ConnectionId, connectionId, StringComparison.Ordinal))
                    {
                        // Its persisted-player load will not be used, unless another connection's
                        // join of the same player shares it (prefetches are keyed by player name):
                        // a takeover releases the old connection while the new one's join, whose
                        // load is already Ready, is being routed.
                        string playerName = _pendingJoins[i].PlayerName;
                        AccountKey? joinAccount = _pendingJoins[i].Request.ResolvedAccount;
                        _pendingJoins.RemoveAt(i);
                        if (!IsPlayerPrefetchSharedByAnotherJoin(playerName, joinAccount, connectionId))
                        {
                            Runtime?.DiscardPersistedPlayerPrefetch(playerName, joinAccount);
                        }
                    }
                }
            }

            if (_connectionClaims.TryGetValue(connectionId, out string? sessionId))
            {
                // Claims are written in pairs, so the owner is this connection; the check keeps a
                // stray release from touching a session another connection holds.
                if (_sessionClaims.TryGetValue(sessionId, out string? ownerConnection)
                    && string.Equals(ownerConnection, connectionId, StringComparison.Ordinal))
                {
                    ReleasingSessionForTest?.Invoke(sessionId);
                    // The departed session's queued actions would otherwise wait forever.
                    _actionQueue.RemoveSession(new PlayerSessionId(sessionId));
                    _commandInbox.Remove(new PlayerSessionId(sessionId));
                    PurgePendingActionResults(sessionId);
                    if (_authority is RuntimeAuthoritativeSessionHost runtimeAuthority)
                    {
                        runtimeAuthority.ReleaseSession(sessionId);
                    }

                    _sessionClaims.TryRemove(sessionId, out _);
                }

                _connectionClaims.TryRemove(connectionId, out _);
            }

            if (_connections.TryRemove(connectionId, out _))
            {
                Volatile.Write(ref _connectionsChanged, 1);
            }
        }
    }

    // Takes the tick lock, so it waits for an in-flight tick, message or release (one still
    // removing a Bullet body on a connection loop's thread), and sets _disposed under it: every
    // later entry point sees the flag under the same lock and leaves the runtime alone. The
    // runtime is then disposed outside the lock, which nothing else can enter any more.
    public void Dispose()
    {
        lock (_authoritySync)
        {
            if (_disposed)
            {
                return;
            }

            Volatile.Write(ref _disposed, true);
        }

        // The leases of the players still here go before the write queue drains.
        Runtime?.ReleaseAllPresence();
        // Finish pending checkpoint writes before the authority disposes its persistence.
        _checkpointWrites?.Dispose();
        switch (_authority)
        {
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    private ServerRoutingContext GetOrCreateConnectionContext(string connectionId)
    {
        if (_connections.TryGetValue(connectionId, out ServerRoutingContext? existing))
        {
            return existing;
        }

        ServerRoutingContext context = _connections.GetOrAdd(
            connectionId,
            static (id, state) => new ServerRoutingContext(
                id,
                state.Authority,
                state.CommandInbox,
                state.ActionQueue,
                state.SessionClaims),
            (Authority: _authority,
                CommandInbox: (IPlayerCommandInbox)_commandInbox,
                ActionQueue: (IPlayerActionQueue)_actionQueue,
                SessionClaims: (IServerSessionClaimRegistry)this));
        Volatile.Write(ref _connectionsChanged, 1);
        return context;
    }

    private void RunTick(ulong tick, float fixedDeltaSeconds)
    {
        StepAccountStandings(Runtime, tick);
        ApplyPendingJoins();
        RefreshJoinedSessionIds();
        _commandInbox.DrainInto(_joinedSessionIds, _tickCommands);
        _actionQueue.DrainInto(_joinedSessionIds, _tickActions);
        IReadOnlyList<AuthoritativeActionResult> actionResults = _tickDrivenAuthority!.AdvanceTick(
            tick,
            fixedDeltaSeconds,
            _tickCommands,
            _tickActions,
            DefaultServerTuning,
            allowGameplaySimulation: true);
        for (int i = 0; i < actionResults.Count; i++)
        {
            _pendingActionResults.Enqueue(actionResults[i]);
        }

        WorldSessionRuntime? runtime = Runtime;
        if (runtime != null)
        {
            _checkpointElapsedSeconds += fixedDeltaSeconds;
            while (_checkpointElapsedSeconds >= 5d)
            {
                // Capture is in-memory; the database write runs off the tick thread.
                if (_checkpointWrites != null &&
                    runtime.CapturePersistenceCheckpoint() is { } checkpoint)
                {
                    _checkpointWrites.Enqueue(checkpoint);
                }

                _checkpointElapsedSeconds -= 5d;
            }
        }

        ServePendingWorldResyncs();
        ServePendingInventoryPushes(runtime);
        ServePendingGroundItems(runtime);
        ServePendingVitals(runtime);
        ServePendingSessionMessages(runtime);
    }

    // M4: mana, max mana and running cooldowns, to the owning session only, outside the action results.
    // The runtime decides on a 16-tick cadence which sessions changed; a steady tick reads a modulus.
    private void ServePendingVitals(WorldSessionRuntime? runtime)
    {
        if (runtime == null || !runtime.IsVitalsDrainTick)
        {
            return;
        }

        runtime.DrainVitalsInto(_vitalsScratch, _isSessionClaimed);
        for (int i = 0; i < _vitalsScratch.Count; i++)
        {
            SessionVitals vitals = _vitalsScratch[i];
            if (_sessionClaims.TryGetValue(vitals.SessionId.Value, out string? connectionId))
            {
                _pendingConnectionReplies.Enqueue(new PendingConnectionReply(connectionId, vitals.Message));
            }
        }

        _vitalsScratch.Clear();
    }

    // M5, M6 and M8: the generic server pushes (party, trade, quest log, owner progress), each to its own
    // session's connection, outside the action results. A message of a session with no connection yet
    // stays queued in the runtime. A steady tick reads a count.
    private void ServePendingSessionMessages(WorldSessionRuntime? runtime)
    {
        if (runtime == null || !runtime.HasPendingSessionMessages)
        {
            return;
        }

        runtime.DrainSessionMessagesInto(_sessionMessageScratch, _isSessionClaimed);
        for (int i = 0; i < _sessionMessageScratch.Count; i++)
        {
            SessionServerMessage entry = _sessionMessageScratch[i];
            if (_sessionClaims.TryGetValue(entry.SessionId.Value, out string? connectionId))
            {
                _pendingConnectionReplies.Enqueue(new PendingConnectionReply(connectionId, entry.Message));
            }
        }

        _sessionMessageScratch.Clear();
    }

    // M3: ground items that appeared, changed or went since the last tick, to every claimed session. Delta
    // only (a steady tick reads a flag), outside the action results because kills sit inside the tick
    // golden. Upserts and removals are idempotent, so a joiner that also gets a Reset sees no conflict.
    private void ServePendingGroundItems(WorldSessionRuntime? runtime)
    {
        if (runtime == null || !runtime.HasPendingGroundDeltas)
        {
            return;
        }

        runtime.DrainGroundDeltas(_groundUpsertScratch, _groundRemovedScratch);
        if (_groundUpsertScratch.Count > 0 || _groundRemovedScratch.Count > 0)
        {
            List<ServerGroundItems> messages = BuildGroundItemMessages(reset: false, _groundUpsertScratch, _groundRemovedScratch);
            foreach (string connectionId in _sessionClaims.Values)
            {
                for (int i = 0; i < messages.Count; i++)
                {
                    _pendingConnectionReplies.Enqueue(new PendingConnectionReply(connectionId, messages[i]));
                }
            }
        }

        _groundUpsertScratch.Clear();
        _groundRemovedScratch.Clear();
    }

    // The full ground list for a connection that just joined: a Reset first, then more messages when the
    // list is larger than one message holds. Nothing when the ground is empty (a joiner starts empty).
    private void EnqueueGroundReset(string connectionId, WorldSessionRuntime? runtime)
    {
        if (runtime == null || runtime.GroundItemCount == 0)
        {
            return;
        }

        List<GroundItemSnapshot> all = new(runtime.GroundItemCount);
        runtime.CaptureGroundItems(all);
        List<ServerGroundItems> messages = BuildGroundItemMessages(reset: true, all, []);
        for (int i = 0; i < messages.Count; i++)
        {
            _pendingConnectionReplies.Enqueue(new PendingConnectionReply(connectionId, messages[i]));
        }
    }

    // Splits into messages of at most MaxEntries entries (upserts and removals together); Reset only on
    // the first.
    private static List<ServerGroundItems> BuildGroundItemMessages(
        bool reset,
        List<GroundItemSnapshot> upserts,
        List<long> removedIds)
    {
        List<ServerGroundItems> messages = [];
        int upsertIndex = 0;
        int removedIndex = 0;
        bool first = true;
        while (first || upsertIndex < upserts.Count || removedIndex < removedIds.Count)
        {
            int room = ServerGroundItems.MaxEntries;
            int upsertCount = Math.Min(room, upserts.Count - upsertIndex);
            room -= upsertCount;
            int removedCount = Math.Min(room, removedIds.Count - removedIndex);
            messages.Add(new ServerGroundItems(
                reset && first,
                upserts.GetRange(upsertIndex, upsertCount),
                removedIds.GetRange(removedIndex, removedCount)));
            upsertIndex += upsertCount;
            removedIndex += removedCount;
            first = false;
        }

        return messages;
    }

    private readonly List<GroundItemSnapshot> _groundUpsertScratch = [];
    private readonly List<long> _groundRemovedScratch = [];

    // Wave I: loot gained or lost this tick, one push per killer. It travels the pending-connection-reply
    // route (delivered before the tick's snapshot), not the action results, because kills happen inside
    // the tick golden. A session with no claim (it left) drops its push. Steady-state ticks only read a
    // count.
    //
    // An inventory action's result waits for the next snapshot and carries the inventory of its own
    // tick, while a push goes out at once. The client keeps whichever it applies last, so a push that
    // overtook such a result would be overwritten by the older inventory and the looted item would
    // vanish from the client until its next inventory change. So a session's push waits while an
    // inventory result of that same session is queued (the order only matters on one connection); every
    // other session's push goes out on this tick. The held push stays pending in the runtime and is
    // sent on the first push tick after the snapshot has delivered the result, so a session is held
    // until the first post-snapshot tick with no new result of that session (the result queue is
    // emptied by each snapshot, but a session's own result on the first tick after one re-holds it);
    // the snapshot is built when the push is sent, so it stays the newest. (An earlier version held
    // every session's push while any session had a result queued, which let one player delay
    // everybody's loot.)
    private void ServePendingInventoryPushes(WorldSessionRuntime? runtime)
    {
        if (runtime == null || !runtime.HasPendingInventoryPushes)
        {
            return;
        }

        CollectSessionsWithUndeliveredInventoryResult(_heldPushSessions);
        runtime.DrainInventoryPushesInto(_inventoryPushScratch, _heldPushSessions);
        _heldPushSessions.Clear();
        for (int i = 0; i < _inventoryPushScratch.Count; i++)
        {
            SessionInventoryPush push = _inventoryPushScratch[i];
            if (_sessionClaims.TryGetValue(push.SessionId.Value, out string? connectionId))
            {
                _pendingConnectionReplies.Enqueue(new PendingConnectionReply(connectionId, push.Message));
            }
        }

        _inventoryPushScratch.Clear();
    }

    // Runs only on a tick with a push pending, so its enumerator does not touch steady-state ticks.
    private void CollectSessionsWithUndeliveredInventoryResult(HashSet<string> destination)
    {
        foreach (AuthoritativeActionResult result in _pendingActionResults)
        {
            if (result.Payload is InventoryChangedResult)
            {
                destination.Add(result.SessionId);
            }
        }
    }

    // WorldResyncRequestHandler only marks a connection; the full-world export runs here, on the
    // tick thread after the tick, and the snapshot goes out through the pending-reply drain. With
    // no pending resync this only reads a flag per connection, so steady-state ticks do not allocate.
    private void ServePendingWorldResyncs()
    {
        IWorldResyncSource? source = _authority as IWorldResyncSource;
        for (int i = 0; i < _connectionContexts.Count; i++)
        {
            ServerRoutingContext context = _connectionContexts[i];
            if (!context.TryTakeWorldResyncRequest()
                || source == null
                || context.LastJoinRequest == null
                || !_connections.ContainsKey(context.ConnectionId))
            {
                continue;
            }

            _pendingConnectionReplies.Enqueue(new PendingConnectionReply(
                context.ConnectionId,
                new ServerSnapshotMessage(source.ExportWorldResync())));
        }
    }

    // Queues a join request for the next tick and starts its player's persisted-state load on the
    // persistence worker. Nothing waits for the load here: the receive thread (a thread-pool
    // thread) returns at once, and the tick defers the join until the load finished (see
    // ApplyPendingJoins), so the tick never flushes the write queue or reads the database. The load
    // is read-only, so a join that is never applied creates no profile row. A second join request
    // from a connection whose first one is still pending is ignored: one connection cannot pile up
    // joins (or loads). Called under _authoritySync.
    private void QueueJoin(string connectionId, ClientJoinRequestMessage request)
    {
        for (int i = 0; i < _pendingJoins.Count; i++)
        {
            if (string.Equals(_pendingJoins[i].ConnectionId, connectionId, StringComparison.Ordinal))
            {
                Dream.Multiplayer.Diagnostics.NetworkDiagnosticsLog.Write(
                    "server.join.duplicate-ignored",
                    $"connection={connectionId} session={request.RequestedSessionId}");
                return;
            }
        }

        string playerName = WorldSessionRuntime.ResolveJoinPlayerName(request.RequestedSessionId, request.DisplayName);
        Runtime?.BeginPersistedPlayerPrefetch(playerName, request.ResolvedAccount);
        _pendingJoins.Add(new PendingJoin(connectionId, request, playerName, DeferredTicks: 0, PrefetchRetried: false));
    }

    // Same-tick joins in canonical order. A join whose connection was dropped is skipped. A join is
    // routed only once its player's persisted state is loaded, checked right before it is routed
    // (an earlier join of the same player in this batch consumes the load, and the later one then
    // waits for a fresh load). Otherwise it waits for a later tick:
    //  - load still pending (or missing, then restarted): deferred, at most
    //    MaxJoinPrefetchDeferralTicks, then rejected with JoinRetryRejectionReason;
    //  - load failed: restarted once, then rejected with JoinRetryRejectionReason.
    // A rejected join leaves the connection open and unclaimed, so the client may send it again.
    // The tick never reads the database. With no pending join this returns before touching
    // anything, so steady-state ticks do not allocate.
    private void ApplyPendingJoins()
    {
        if (_pendingJoins.Count == 0)
        {
            return;
        }

        List<PendingJoin> joins = _pendingJoins;
        _pendingJoins = _applyingJoins;
        _applyingJoins = joins;
        if (joins.Count > 1)
        {
            joins.Sort(ComparePendingJoinsOrdinal);
        }

        WorldSessionRuntime? runtime = Runtime;
        for (int i = 0; i < joins.Count; i++)
        {
            PendingJoin join = joins[i];
            if (!_connections.TryGetValue(join.ConnectionId, out ServerRoutingContext? context))
            {
                runtime?.DiscardPersistedPlayerPrefetch(join.PlayerName, join.Request.ResolvedAccount);
                continue;
            }

            PersistedPlayerLoadState loadState = runtime?.GetPersistedPlayerLoadState(join.PlayerName, join.Request.ResolvedAccount)
                ?? PersistedPlayerLoadState.Ready;
            switch (loadState)
            {
                case PersistedPlayerLoadState.Ready:
                    break;
                case PersistedPlayerLoadState.Failed when !join.PrefetchRetried:
                    runtime!.BeginPersistedPlayerPrefetch(join.PlayerName, join.Request.ResolvedAccount);
                    _pendingJoins.Add(join with { DeferredTicks = join.DeferredTicks + 1, PrefetchRetried = true });
                    continue;
                case PersistedPlayerLoadState.Failed:
                    RejectJoinForRetry(join, "prefetch-failed");
                    continue;
                default:
                    if (join.DeferredTicks >= MaxJoinPrefetchDeferralTicks)
                    {
                        RejectJoinForRetry(join, "prefetch-timeout");
                        continue;
                    }

                    if (loadState == PersistedPlayerLoadState.Missing)
                    {
                        runtime!.BeginPersistedPlayerPrefetch(join.PlayerName, join.Request.ResolvedAccount);
                    }

                    _pendingJoins.Add(join with { DeferredTicks = join.DeferredTicks + 1 });
                    continue;
            }

            // A ban set here since this join's load started: refused without routing it.
            if (RecentBanText(join.Request.ResolvedAccount) is { } recentBan)
            {
                runtime?.DiscardPersistedPlayerPrefetch(join.PlayerName, join.Request.ResolvedAccount);
                _pendingConnectionReplies.Enqueue(new PendingConnectionReply(join.ConnectionId, new JoinRejectedMessage(recentBan)));
                continue;
            }

            context.ClearOutboundMessages();
            context.ClearJoinConflict();
            _router.RouteServerInbound(context, join.Request);
            CompleteJoinAttempt(context, join.Request);
            for (int replyIndex = 0; replyIndex < context.OutboundMessages.Count; replyIndex++)
            {
                _pendingConnectionReplies.Enqueue(new PendingConnectionReply(join.ConnectionId, context.OutboundMessages[replyIndex]));
            }

            bool accepted = _connectionClaims.ContainsKey(join.ConnectionId) && HasJoinAccepted(context.OutboundMessages);
            context.ClearOutboundMessages();
            if (_connectionClaims.ContainsKey(join.ConnectionId))
            {
                EnqueueGroundReset(join.ConnectionId, runtime);
            }

            if (accepted)
            {
                // The message of the day follows the join reply.
                if (CompleteAcceptedJoin(context) is { } motd)
                {
                    _pendingConnectionReplies.Enqueue(new PendingConnectionReply(join.ConnectionId, motd));
                }
            }
            else if (!_connectionClaims.ContainsKey(join.ConnectionId))
            {
                // Refused after the load took the character's lease: let it go.
                runtime?.ReleasePresenceOfRejectedJoin(join.PlayerName, join.Request.ResolvedAccount);
            }

            // An accepted join consumed its load; a rejected one (or one that restored nothing)
            // drops it, so a later join of this player reads fresh rows.
            runtime?.DiscardPersistedPlayerPrefetch(join.PlayerName, join.Request.ResolvedAccount);
        }

        joins.Clear();
    }

    private void RejectJoinForRetry(PendingJoin join, string reason)
    {
        Runtime?.DiscardPersistedPlayerPrefetch(join.PlayerName, join.Request.ResolvedAccount);
        _pendingConnectionReplies.Enqueue(new PendingConnectionReply(
            join.ConnectionId,
            new JoinRejectedMessage(JoinRetryRejectionReason)));
        Dream.Multiplayer.Diagnostics.NetworkDiagnosticsLog.Write(
            "server.join.prefetch-rejected",
            $"connection={join.ConnectionId} session={join.Request.RequestedSessionId} player={join.PlayerName} reason={reason} deferredTicks={join.DeferredTicks}");
    }

    private readonly record struct PendingJoin(
        string ConnectionId,
        ClientJoinRequestMessage Request,
        string PlayerName,
        int DeferredTicks,
        bool PrefetchRetried);

    // Connections are added and removed off the tick thread. Enumerating the ConcurrentDictionary
    // is safe during those changes, and a change made mid-enumeration sets the flag again for the
    // next tick. Join state is re-read every tick because LastJoinRequest changes on its own, and
    // the result is sorted in place (ordinal), so the command drain, the runtime tick and the
    // synthesized primary-use actions see sessions in the same order on every server process.
    private void RefreshJoinedSessionIds()
    {
        if (Interlocked.Exchange(ref _connectionsChanged, 0) != 0)
        {
            _connectionContexts.Clear();
            foreach (KeyValuePair<string, ServerRoutingContext> connection in _connections)
            {
                _connectionContexts.Add(connection.Value);
            }
        }

        _joinedSessionIds.Clear();
        for (int i = 0; i < _connectionContexts.Count; i++)
        {
            if (_connectionContexts[i].LastJoinRequest is { } join)
            {
                _joinedSessionIds.Add(new PlayerSessionId(join.RequestedSessionId));
            }
        }

        if (_joinedSessionIds.Count > 1)
        {
            _joinedSessionIds.Sort(CompareSessionIdsOrdinal);
        }
    }
}
