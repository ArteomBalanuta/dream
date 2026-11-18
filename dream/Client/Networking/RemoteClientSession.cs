using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Dream.Client.Replication;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Simulation.Players;
using Dream.Simulation.World;

namespace Dream.Client.Networking;


public sealed class RemoteClientSession : IAsyncDisposable
{
    private readonly ITransportConnection _connection;
    private readonly IProtocolCodec _codec;
    private readonly IMessageFramer _framer;
    private readonly byte[] _receiveChunk = new byte[16 * 1024];
    private readonly FrameReceiveBuffer _receiveBuffer = new();
    private readonly ConcurrentQueue<IServerToClientMessage> _inboundMessages = new();
    // SCALE-2: rebuilds full actors from delta-coded snapshots as they are decoded, in wire order,
    // so every consumer of the inbound queue sees full actors. Receive loop only.
    private readonly ActorStateCache _actorStates = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private Task? _receiveLoopTask;
    // Outbound traffic from the render thread is only enqueued; one writer task sends it, so a
    // full socket buffer can no longer block a frame. Commands (one per prediction tick) go out in
    // order through a bounded FIFO that merges its oldest frames on overflow; actions keep their
    // order too.
    private readonly ClientOutboundCommandQueue _pendingCommands = new();
    private readonly ConcurrentQueue<IClientToServerMessage> _pendingMessages = new();
    private readonly SemaphoreSlim _outboundSignal = new(0, int.MaxValue);
    private readonly object _sendLoopSync = new();
    private Task? _sendLoopTask;
    // One reusable pooled buffer for every outbound frame; only touched while _sendLock is held,
    // including its Dispose in DisposeAsync.
    private readonly PooledFrameWriter _frameWriter = new();
    // 0 until DisposeAsync starts, then 1. Checked by senders before and after taking _sendLock.
    private int _disposeState;
    private readonly string _commandLogKey;
    private readonly string _slowCommandLogKey;
    private readonly string _keepaliveLogKey;
    private long _sentCommandCount;
    // Assigned under _sendLock together with the encode, so sequences follow wire order.
    private ulong _nextOutboundSequence = 1;
    // Stopwatch timestamp of the last frame handed to the transport (any kind, keepalives too).
    private long _lastSendTimestamp = Stopwatch.GetTimestamp();
    // Set by the keepalive task; the send loop takes it and writes an empty frame if nothing else
    // went out meanwhile.
    private int _keepaliveDue;
    private long _sentKeepaliveCount;
    private Task? _keepaliveTask;
    // Completed by the receive loop: with the server's hello when it arrives, with a
    // ProtocolVersionMismatchException when the server speaks another version, or with null when
    // the loop ends first. ConnectAsync waits on it briefly.
    private readonly TaskCompletionSource<ServerHelloMessage?> _serverHello =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Join supervision (see JoinSupervisorAsync). The receive loop hands every JoinAccepted and
    // JoinRejected to the supervisor until the join settles, and completes the channel when it ends.
    private readonly TimeProvider _timeProvider;
    private readonly Channel<IServerToClientMessage> _joinReplies =
        Channel.CreateUnbounded<IServerToClientMessage>(new UnboundedChannelOptions { SingleReader = true });
    private int _joinSupervisorStarted;
    private volatile bool _joinSettled;
    private int _joinRetryCount;
    private Task? _joinSupervisorTask;
    // Server-silence watchdog (see ServerSilenceWatchdogAsync). _lastInboundTimestamp is a
    // _timeProvider timestamp written by the receive loop for every socket read that returned
    // bytes, so a large frame (a multi-MB world resync on a slow link) that takes longer than
    // ServerSilenceTimeout to arrive in full still counts as data while it trickles in.
    private readonly TaskCompletionSource _joinAccepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _lastInboundTimestamp;
    private long _inboundFrameCount;
    private Task? _watchdogTask;
    // SCALE-1 chunked world transfer (receive loop only). A JoinAccepted or world snapshot that
    // announces a transfer (WorldTransferId) is held with every message after it until the
    // transfer's final server.world-chunk arrives; then the carrier goes into the inbound queue
    // with the assembled world, followed by the held messages in arrival order. Consumers so see
    // the protocol v2 order, and world mutations in the held snapshots apply on top of the world.
    // The transfer is bounded (WorldTransferLimits: items, held messages, a deadline on the
    // injected TimeProvider, checked for every drained frame); a breach ends the session with
    // "World transfer failed: ..." like any other protocol error.
    private readonly WorldTransferReassembler _worldTransfers;
    private readonly Action<IServerToClientMessage> _deliverInbound;
    private long _worldTransfersCompleted;
    private long _worldTransferStartedTimestamp;
    // Payload bytes of the transfer in progress (its carrier and chunks; held messages are not
    // counted). Written by the receive loop, read by the HUD through TryGetWorldTransferProgress.
    private long _worldTransferBytes;

    // How long ConnectAsync waits for the server's hello before returning the session anyway.
    public static readonly TimeSpan DefaultServerHelloTimeout = TimeSpan.FromSeconds(2);

    // Commands go out only from prediction ticks on the game loop, so a frozen loop (a window
    // drag, a long GC, a debugger break) sends nothing and the server's idle timeout
    // (ServerConnectionOptions.IdleTimeout, 10 s) would close the connection. Once the background
    // receive starts, a keepalive task checks twice per interval and, when no frame has gone out
    // for KeepaliveInterval, has the send loop write one empty frame (a zero length prefix). The
    // server counts any complete frame as liveness and skips empty ones, so this needs no new
    // message kind and no protocol version change. A dead process sends nothing and is still
    // closed by the server.
    public static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(1);

    // The server answers a join whose saved data could not be loaded in time with JoinRejected
    // (DedicatedServerHost.JoinRetryRejectionReason) and keeps the connection open, so the join
    // request is sent again on the same connection after JoinRetryBaseDelay, doubling each time,
    // up to MaxJoinRetries times. Any other rejection, a last retry rejected, or no JoinAccepted
    // within JoinTimeout of the first request ends the session with a DisconnectReason; the
    // keepalive would otherwise keep an unjoined client connected forever. The waits use the
    // injected TimeProvider; the keepalive does not.
    public const int MaxJoinRetries = 3;
    public static readonly TimeSpan JoinRetryBaseDelay = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(45);

    // The server sends a snapshot every tick or two (32-64 Hz), so a joined session that receives
    // no data at all for ServerSilenceTimeout is talking to a hung or vanished server whose socket
    // never closed (a frozen process, a dropped route). The watchdog then ends the session the way
    // a lost connection does, with ServerSilenceDisconnectReason. Only inbound bytes count: the
    // client's own keepalives prove nothing about the server. The wait uses the injected
    // TimeProvider.
    public static readonly TimeSpan ServerSilenceTimeout = TimeSpan.FromSeconds(8);
    public const string ServerSilenceDisconnectReason = "Connection lost: no data from the server for 8 s";

    // How long DisposeAsync waits for each background loop, and for a direct send holding the frame
    // buffer, before it gives up on that one and carries on with the teardown. Cancelling _shutdown
    // normally ends them in microseconds -- a pending socket receive included, which .NET aborts on
    // the token -- so this is only the bound that keeps a loop that does not from freezing the
    // render thread that joins DisposeAsync. Matched to the server's own
    // DedicatedServerService.ConnectionLoopShutdownTimeout.
    public static readonly TimeSpan LoopShutdownTimeout = TimeSpan.FromSeconds(5);

    // The prefix of every reason a session ends with when the link broke, as opposed to the server
    // refusing or removing the player (a join rejection, a kick, a ban, a protocol mismatch).
    public const string ConnectionLostPrefix = "Connection lost:";

    // Whether a session that ended with this reason is worth reconnecting: only a lost connection is.
    public static bool IsRetryableEnd(string? reason) =>
        reason != null && reason.StartsWith(ConnectionLostPrefix, StringComparison.Ordinal);

    // The bounds this session receives world transfers under.
    public WorldTransferLimits WorldTransferLimits => _worldTransfers.Limits;

    public RemoteClientSession(
        ITransportConnection connection,
        IProtocolCodec codec,
        IMessageFramer framer,
        RemoteClientOptions options,
        TimeProvider? timeProvider = null,
        WorldTransferLimits? worldTransferLimits = null)
    {
        _connection = connection;
        _codec = codec;
        _framer = framer;
        Options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _worldTransfers = new WorldTransferReassembler(worldTransferLimits ?? WorldTransferLimits.Default, _timeProvider);
        _commandLogKey = $"client.tx.command.{options.SessionId}";
        _slowCommandLogKey = $"client.tx.command.slow.{options.SessionId}";
        _keepaliveLogKey = $"client.tx.keepalive.{options.SessionId}";
        _deliverInbound = DeliverInbound;
    }

    public RemoteClientOptions Options { get; }

    public string SessionId => Options.SessionId;

    public bool IsConnected { get; private set; } = true;

    // Why the connection stopped, when it stopped other than by this session's own disposal; for
    // example "Server uses protocol v1; this client uses v2.", or "Connection lost: ..." when the
    // server closed the socket (an idle or stall close) or a send failed. Null while connected.
    // Also set when the join fails: "Join rejected: ..." or "Join timed out: ...", and by the
    // server-silence watchdog (ServerSilenceDisconnectReason).
    //
    // The send loop, the receive loop and the owner thread can all end the same session at once, so this is
    // one field written under Interlocked rather than a plain `??=`: a read-then-write let a late
    // "Connection lost: ..." overwrite a protocol mismatch, which turns a non-retryable end into a retryable
    // one and sends the connection screen into an auto-reconnect loop against a server it cannot speak to.
    public string? DisconnectReason => Volatile.Read(ref _disconnectReason);

    private string? _disconnectReason;

    // First writer wins, atomically.
    private void TrySetDisconnectReason(string reason) =>
        Interlocked.CompareExchange(ref _disconnectReason, reason, null);

    // A protocol or world-transfer fault is the real cause and replaces whatever the dying socket recorded
    // (what the unconditional assignments on those two paths have always meant).
    private void SetDisconnectReasonOutright(string reason) =>
        Volatile.Write(ref _disconnectReason, reason);

    // True once the server accepted this session's join.
    public bool IsJoined { get; private set; }

    // Join requests re-sent after a retryable rejection.
    public int JoinRetryCount => Volatile.Read(ref _joinRetryCount);

    // Complete inbound frames processed so far, empty ones included (tests). A frame counts once it
    // was decoded, accepted (or held by a world transfer) and the transfer bounds were checked, so
    // a test that waits for the count can move a manual clock without racing the frame's handling.
    // A frame whose handling ended the session is not counted.
    internal long InboundFrameCount => Interlocked.Read(ref _inboundFrameCount);

    // Read only while the receive loop is idle (tests).
    internal ActorStateCache ActorStates => _actorStates;

    // Chunked world transfers (joins and resyncs) completed and delivered.
    public long WorldTransfersCompleted => Interlocked.Read(ref _worldTransfersCompleted);

    // True while a world transfer is being received; its carrier and later messages are held.
    public bool IsReceivingWorld => _worldTransfers.Current != null;

    // The transfer in progress, for the HUD's "receiving world" line: payload bytes of its carrier
    // and chunks so far, chunks received, and the time since its carrier arrived (on the session's
    // TimeProvider). The chunk count of the whole transfer is only known at its final chunk, so
    // there is no total. False when no transfer is in progress. Safe to call from the render
    // thread while the receive loop runs (the values may be a frame stale); allocates nothing.
    public bool TryGetWorldTransferProgress(out long bytesReceived, out int chunksReceived, out TimeSpan elapsed)
    {
        WorldTransferReceiver? current = _worldTransfers.Current;
        if (current == null)
        {
            bytesReceived = 0;
            chunksReceived = 0;
            elapsed = TimeSpan.Zero;
            return false;
        }

        bytesReceived = Interlocked.Read(ref _worldTransferBytes);
        chunksReceived = current.ChunksReceived;
        elapsed = _timeProvider.GetElapsedTime(current.StartedTimestamp);
        return true;
    }

    public static async ValueTask<RemoteClientSession> ConnectAsync(
        RemoteClientOptions options,
        IClientTransport? transport = null,
        IProtocolCodec? codec = null,
        IMessageFramer? framer = null,
        CancellationToken cancellationToken = default,
        TimeSpan? serverHelloTimeout = null,
        TimeProvider? timeProvider = null)
    {
        transport ??= new TcpClientConnector();
        codec ??= new TransportJsonCodec();
        framer ??= new LengthPrefixedMessageFramer();

        NetworkDiagnosticsLog.Write(
            "client.connect.start",
            $"endpoint={options.EndpointLabel} session={options.SessionId} name={options.DisplayName}");
        Stopwatch? stopwatch = NetworkDiagnosticsLog.IsEnabled ? Stopwatch.StartNew() : null;
        ITransportConnection connection = await transport.ConnectAsync(
            options.Host,
            options.Port,
            cancellationToken);
        stopwatch?.Stop();
        NetworkDiagnosticsLog.Write(
            "client.connect.transport",
            $"endpoint={options.EndpointLabel} connection={connection.ConnectionId} connectMs={stopwatch?.Elapsed.TotalMilliseconds ?? 0d:0.00}");
        RemoteClientSession session = new(connection, codec, framer, options, timeProvider);
        await session.SendHelloAsync(cancellationToken);
        await session.SendJoinRequestAsync(cancellationToken);
        session.StartBackgroundReceive();
        await session.AwaitCompatibleServerAsync(serverHelloTimeout ?? DefaultServerHelloTimeout, cancellationToken);
        NetworkDiagnosticsLog.Write(
            "client.connect.ready",
            $"endpoint={options.EndpointLabel} connection={connection.ConnectionId} session={options.SessionId}");
        return session;
    }

    // Fails the connect when the server speaks another protocol version, so the connection
    // screen shows that reason instead of a bare disconnect. A server that closes without a
    // hello, or answers slower than the timeout, still yields the session as before; a mismatch
    // found later still stops the receive loop and sets DisconnectReason.
    private async ValueTask AwaitCompatibleServerAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            // On the session's clock, as the join supervisor's JoinTimeout is: with a manual clock
            // the hello timeout must not run on the wall clock while the join timeout runs on the
            // manual one (wave 11: an advancing manual clock passed JoinTimeout first).
            await _serverHello.Task.WaitAsync(timeout, _timeProvider, cancellationToken);
        }
        catch (TimeoutException)
        {
            NetworkDiagnosticsLog.Write(
                "client.connect.hello-timeout",
                $"endpoint={Options.EndpointLabel} timeoutMs={timeout.TotalMilliseconds:0}");
        }
        catch (Exception ex) when (ex is ProtocolVersionMismatchException or OperationCanceledException)
        {
            await DisposeAsync();
            throw;
        }
    }

    public async ValueTask SendHelloAsync(CancellationToken cancellationToken = default)
    {
        await SendMessageAsync(new ClientHelloMessage(Options.ClientName), cancellationToken);
    }

    // The first call also starts the join supervisor, whose timeout counts from here.
    public async ValueTask SendJoinRequestAsync(CancellationToken cancellationToken = default)
    {
        StartJoinSupervisor();
        await SendMessageAsync(
            new ClientJoinRequestMessage(Options.SessionId, Options.DisplayName, Options.AccountToken, Options.HoldSpawnProtection ? true : null),
            cancellationToken);
    }

    private void StartJoinSupervisor()
    {
        if (Volatile.Read(ref _disposeState) != 0 ||
            Interlocked.Exchange(ref _joinSupervisorStarted, 1) != 0)
        {
            return;
        }

        long firstRequestTimestamp = _timeProvider.GetTimestamp();
        CancellationToken shutdownToken;
        try
        {
            shutdownToken = _shutdown.Token;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        Volatile.Write(
            ref _joinSupervisorTask,
            Task.Run(() => JoinSupervisorAsync(firstRequestTimestamp, shutdownToken)));
    }

    // Waits for the join's outcome. A retryable rejection re-sends the request through the send
    // loop after a backoff (1 s, 2 s, 4 s); anything else that is not JoinAccepted ends the session.
    // Stops quietly when the connection ends first (the receive loop set the reason) or on dispose.
    private async Task JoinSupervisorAsync(long firstRequestTimestamp, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                TimeSpan remaining = JoinTimeout - _timeProvider.GetElapsedTime(firstRequestTimestamp);
                IServerToClientMessage? reply = remaining > TimeSpan.Zero
                    ? await ReadJoinReplyAsync(remaining, cancellationToken)
                    : null;
                if (reply == null)
                {
                    EndSessionForJoin(
                        $"Join timed out: the server did not accept the join within {JoinTimeout.TotalSeconds:0} s.");
                    return;
                }

                if (reply is JoinAcceptedMessage)
                {
                    return;
                }

                string reason = ((JoinRejectedMessage)reply).Reason;
                int retries = Volatile.Read(ref _joinRetryCount);
                if (!string.Equals(reason, DedicatedServerHost.JoinRetryRejectionReason, StringComparison.Ordinal))
                {
                    EndSessionForJoin($"Join rejected: {reason}");
                    return;
                }

                if (retries >= MaxJoinRetries)
                {
                    EndSessionForJoin($"Join rejected after {retries + 1} attempts: {reason}");
                    return;
                }

                TimeSpan delay = JoinRetryBaseDelay * (1 << retries);
                if (delay >= JoinTimeout - _timeProvider.GetElapsedTime(firstRequestTimestamp))
                {
                    EndSessionForJoin(
                        $"Join timed out: the server did not accept the join within {JoinTimeout.TotalSeconds:0} s.");
                    return;
                }

                NetworkDiagnosticsLog.Write(
                    "client.join.retry",
                    $"endpoint={Options.EndpointLabel} session={Options.SessionId} attempt={retries + 2} delayMs={delay.TotalMilliseconds:0} reason={reason}");
                await Task.Delay(delay, _timeProvider, cancellationToken);
                Interlocked.Increment(ref _joinRetryCount);
                _pendingMessages.Enqueue(new ClientJoinRequestMessage(Options.SessionId, Options.DisplayName, Options.AccountToken, Options.HoldSpawnProtection ? true : null));
                SignalSendLoop();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ChannelClosedException)
        {
        }
    }

    // The next join reply, or null when none arrives within timeout.
    private async ValueTask<IServerToClientMessage?> ReadJoinReplyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = new(timeout, _timeProvider);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            return await _joinReplies.Reader.ReadAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private void EndSessionForJoin(string reason)
    {
        _joinSettled = true;
        EndSession(reason, "client.join.failed");
    }

    public const string UnsupportedWorldLogEvent = "client.join.unsupported-world";
    public const string ProtocolErrorLogEvent = "client.join.protocol-error";

    // For the owner when it cannot use what the server sent (an unsupported world, or a payload that
    // does not match its own header): ends the session with the reason, like any other failed join,
    // and logs logEvent (UnsupportedWorldLogEvent or ProtocolErrorLogEvent).
    public void EndSessionWithReason(string reason, string logEvent)
    {
        ArgumentException.ThrowIfNullOrEmpty(logEvent);
        EndSession(reason, logEvent);
    }

    // Ends the session the way a lost connection does: IsConnected false with a DisconnectReason
    // the owner shows. The loops stop; the socket closes when the owner disposes the session.
    private void EndSession(string reason, string logEvent)
    {
        if (Volatile.Read(ref _disposeState) != 0)
        {
            return;
        }

        TrySetDisconnectReason(reason);
        IsConnected = false;
        NetworkDiagnosticsLog.Write(
            logEvent,
            $"endpoint={Options.EndpointLabel} session={Options.SessionId} reason={DisconnectReason}");
        try
        {
            _shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async ValueTask SendCommandFrameAsync(PlayerCommandFrame commandFrame, CancellationToken cancellationToken = default)
    {
        await SendMessageAsync(new PlayerCommandMessage(commandFrame), cancellationToken);
    }

    public async ValueTask SendPlayerActionAsync(PlayerActionMessage actionMessage, CancellationToken cancellationToken = default)
    {
        await SendMessageAsync(actionMessage, cancellationToken);
    }

    public long SentCommandCount => Interlocked.Read(ref _sentCommandCount);

    // Empty keepalive frames written because nothing else was sent for KeepaliveInterval.
    public long SentKeepaliveCount => Interlocked.Read(ref _sentKeepaliveCount);

    // Commands queued but not yet handed to the transport.
    public int PendingCommandCount => _pendingCommands.Count;

    // Commands folded into a neighbour because the outbound queue was full.
    public long MergedCommandCount => _pendingCommands.MergedCount;

    // Queues one command for the send loop. Commands are sent in queue order; a command for
    // another session, or one whose sequence is not newer than the last queued, is ignored.
    public void QueueCommandFrame(PlayerCommandFrame commandFrame)
    {
        if (!string.Equals(commandFrame.SessionId.Value, SessionId, StringComparison.Ordinal) ||
            !_pendingCommands.TryEnqueue(commandFrame))
        {
            return;
        }

        SignalSendLoop();
    }

    public void QueueWorldResyncRequest(ulong clientChecksum)
    {
        _pendingMessages.Enqueue(new ClientWorldResyncRequestMessage(clientChecksum));
        SignalSendLoop();
    }

    public void QueueChat(ClientChatMessage chatMessage)
    {
        ArgumentNullException.ThrowIfNull(chatMessage);
        _pendingMessages.Enqueue(chatMessage);
        SignalSendLoop();
    }

    public void QueuePlayerAction(PlayerActionMessage actionMessage)
    {
        ArgumentNullException.ThrowIfNull(actionMessage);
        _pendingMessages.Enqueue(actionMessage);
        SignalSendLoop();
    }

    // Waits until everything queued so far has been written, or the timeout expires.
    public async ValueTask<bool> FlushOutboundAsync(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_pendingMessages.IsEmpty && !_pendingCommands.HasPending && _outboundSignal.CurrentCount == 0 && !_sending)
            {
                return true;
            }

            await Task.Delay(1);
        }

        return false;
    }

    private volatile bool _sending;

    // Wake-ups queued for the send loop and not yet taken (tests).
    internal int PendingSendSignalCount => _outboundSignal.CurrentCount;

    // True while the send loop is inside a pass, e.g. blocked in a transport send (tests).
    internal bool IsSendInFlight => _sending;

    private void SignalSendLoop()
    {
        lock (_sendLoopSync)
        {
            _sendLoopTask ??= Task.Run(() => SendLoopAsync(_shutdown.Token));
        }

        _outboundSignal.Release();
    }

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _outboundSignal.WaitAsync(cancellationToken);
                _sending = true;
                try
                {
                    while (_pendingMessages.TryDequeue(out IClientToServerMessage? message))
                    {
                        await SendMessageAsync(message, cancellationToken);
                    }

                    while (_pendingCommands.TryDequeue(out PlayerCommandFrame command))
                    {
                        await SendMessageAsync(new PlayerCommandMessage(command), cancellationToken);
                        Interlocked.Increment(ref _sentCommandCount);
                    }

                    // Anything just sent above already proved liveness.
                    if (Interlocked.Exchange(ref _keepaliveDue, 0) != 0 &&
                        Stopwatch.GetElapsedTime(Volatile.Read(ref _lastSendTimestamp)) >= KeepaliveInterval)
                    {
                        await SendKeepaliveAsync(cancellationToken);
                    }
                }
                finally
                {
                    _sending = false;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            // DisposeAsync started between the loop's wait and its send; not a transport error.
        }
        catch (Exception ex)
        {
            IsConnected = false;
            TrySetDisconnectReason($"Connection lost: {ex.GetType().Name}: {ex.Message}");
            NetworkDiagnosticsLog.Write(
                "client.send.error",
                $"endpoint={Options.EndpointLabel} session={Options.SessionId} error={ex.GetType().Name}: {ex.Message}");
        }
    }

    public bool TryDequeueInbound(out IServerToClientMessage? message)
    {
        return _inboundMessages.TryDequeue(out message);
    }

    public async ValueTask<bool> ProcessInboundOnceAsync(CancellationToken cancellationToken = default)
    {
        int bytesRead = await _connection.ReceiveAsync(_receiveChunk, cancellationToken);
        if (bytesRead <= 0)
        {
            IsConnected = false;
            return false;
        }

        // Any bytes show the server is alive, even mid-frame; written before a JoinAccepted in
        // this read releases the watchdog, so its first wait counts from the join.
        Volatile.Write(ref _lastInboundTimestamp, _timeProvider.GetTimestamp());
        _receiveBuffer.Append(_receiveChunk.AsSpan(0, bytesRead));
        DrainInboundFrames();
        return true;
    }

    // A decoded snapshot the client cannot apply: one of its collections was absent from the payload
    // (null, not empty), or its actors came neither as a list nor as a frame. Thrown on the receive
    // loop, which ends the session with the reason, like any other malformed frame.
    private static void RejectMalformedSnapshot(ServerSnapshotEnvelope snapshot)
    {
        string? missing =
            snapshot.LastAcceptedCommandSequences == null ? nameof(snapshot.LastAcceptedCommandSequences)
            : snapshot.WorldMutations == null ? nameof(snapshot.WorldMutations)
            : snapshot.DamageEvents == null ? nameof(snapshot.DamageEvents)
            : snapshot.Actors == null && snapshot.ActorFrame == null ? nameof(snapshot.Actors)
            : null;
        if (missing != null)
        {
            throw new InvalidDataException(
                $"Malformed server.snapshot payload: {missing} is missing (tick {snapshot.ServerTick}).");
        }
    }

    private void DrainInboundFrames()
    {
        while (_receiveBuffer.TryReadPayload(_framer, out ReadOnlySpan<byte> payload))
        {
            if (payload.IsEmpty)
            {
                // A transfer whose final chunk never comes would otherwise hold every later message
                // forever while the frames keep the silence watchdog satisfied.
                _worldTransfers.CheckDeadline();
                // Counted once processed (InboundFrameCount).
                Interlocked.Increment(ref _inboundFrameCount);
                continue;
            }

            int payloadBytes = payload.Length;
            DecodedProtocolMessage decoded;
            try
            {
                decoded = _codec.Decode(payload);
            }
            catch (Exception ex) when (ProtocolVersionMismatchException.ReadForeignProtocolVersion(payload) is int serverVersion)
            {
                throw ProtocolVersionMismatchException.FromServer(serverVersion, ex);
            }

            IServerToClientMessage serverMessage = decoded.Message as IServerToClientMessage
                ?? throw new InvalidOperationException(
                    $"Received non-server message kind '{decoded.Message.MessageKind}' on client transport.");
            if (serverMessage is ServerSnapshotMessage snapshotMessage)
            {
                // A snapshot payload that omits one of its collections decodes to null: the decoder
                // keeps that parity with the reference serializer deliberately, so the rejection
                // belongs here, at the client's application boundary. Without it the null reaches the
                // frame thread and throws there instead of ending the session as a protocol error.
                RejectMalformedSnapshot(snapshotMessage.Snapshot);
                // Before any holding (a world transfer), so frames apply in the order they came.
                serverMessage = _actorStates.Apply(snapshotMessage);
            }

            if (serverMessage is ServerHelloMessage hello)
            {
                if (hello.ProtocolVersion != TransportEnvelope.CurrentProtocolVersion)
                {
                    throw ProtocolVersionMismatchException.FromServer(hello.ProtocolVersion);
                }

                _serverHello.TrySetResult(hello);
            }
            else if (!_joinSettled && serverMessage is JoinAcceptedMessage or JoinRejectedMessage)
            {
                if (serverMessage is JoinAcceptedMessage)
                {
                    // The join is settled (no retry, the watchdog starts) as soon as it arrives,
                    // even as a carrier whose world is still to come; IsJoined is set when the
                    // message is delivered (DeliverInbound), with its world.
                    _joinSettled = true;
                    _joinAccepted.TrySetResult();
                }

                _joinReplies.Writer.TryWrite(serverMessage);
            }
            else if (serverMessage is JoinRejectedMessage rejected)
            {
                // A rejection after the join settled (a takeover by another connection): the
                // server closes next, so its reason is the player-facing one. Here, not in
                // DeliverInbound, as the message can be held behind a world transfer.
                TrySetDisconnectReason(rejected.Reason);
            }

            bool wasReceivingWorld = _worldTransfers.Current != null;
            WorldTransferReceiver? completed = _worldTransfers.Accept(serverMessage, _deliverInbound);
            if (serverMessage is WorldChunkMessage or TerrainChunkMessage)
            {
                Interlocked.Add(ref _worldTransferBytes, payloadBytes);
                if (serverMessage is TerrainChunkMessage terrainFragment && NetworkDiagnosticsLog.IsEnabled)
                {
                    NetworkDiagnosticsLog.Write(
                        "client.world-transfer.terrain",
                        $"endpoint={Options.EndpointLabel} session={Options.SessionId} transfer={terrainFragment.TransferId} " +
                        $"fragment={terrainFragment.Index} chunks={terrainFragment.Chunks.Count} final={terrainFragment.IsFinal} bytes={payloadBytes}");
                }
            }

            if (!wasReceivingWorld && _worldTransfers.Current is WorldTransferReceiver started)
            {
                // A new transfer: its carrier is the first of its bytes.
                Interlocked.Exchange(ref _worldTransferBytes, payloadBytes);
                _worldTransferStartedTimestamp = Stopwatch.GetTimestamp();
                if (NetworkDiagnosticsLog.IsEnabled)
                {
                    NetworkDiagnosticsLog.Write(
                        "client.world-transfer.start",
                        $"endpoint={Options.EndpointLabel} session={Options.SessionId} transfer={started.TransferId} carrier={serverMessage.MessageKind}");
                }
            }

            if (completed != null)
            {
                Interlocked.Increment(ref _worldTransfersCompleted);
                if (NetworkDiagnosticsLog.IsEnabled)
                {
                    NetworkDiagnosticsLog.Write(
                        "client.world-transfer.complete",
                        $"endpoint={Options.EndpointLabel} session={Options.SessionId} transfer={completed.TransferId} chunks={completed.ChunksReceived} " +
                        $"terrain={completed.Terrain?.Count ?? 0} held={completed.HeldMessages.Count} ms={Stopwatch.GetElapsedTime(_worldTransferStartedTimestamp).TotalMilliseconds:0.0}");
                }
            }

            LogInbound(decoded.Envelope, serverMessage);
            // Checked after the frame was accepted, so a chunk that arrives right at a bound counts
            // as progress, and a final chunk completes its transfer instead of being rejected. A
            // transfer whose final chunk never comes would otherwise hold every later message
            // forever while the snapshots keep the silence watchdog satisfied.
            _worldTransfers.CheckDeadline();
            // Counted once processed (InboundFrameCount): a test waiting for it may then move its
            // clock without racing this frame's Accept or deadline check.
            Interlocked.Increment(ref _inboundFrameCount);
        }
    }

    // Where the reassembler hands on messages, in order. A JoinAccepted whose world came in a
    // transfer marks the session joined only now, when its consumer can see it.
    private void DeliverInbound(IServerToClientMessage message)
    {
        if (message is JoinAcceptedMessage)
        {
            IsJoined = true;
        }

        _inboundMessages.Enqueue(message);
    }

    // Also starts the keepalive task, so a connected session stays alive while its owner's loop
    // is frozen, and the server-silence watchdog.
    public void StartBackgroundReceive()
    {
        _receiveLoopTask ??= Task.Run(() => ReceiveLoopAsync(_shutdown.Token));
        _keepaliveTask ??= Task.Run(() => KeepaliveLoopAsync(_shutdown.Token));
        _watchdogTask ??= Task.Run(() => ServerSilenceWatchdogAsync(_shutdown.Token));
    }

    // Idle until the join is accepted (before that the join supervisor's timeout applies), then
    // sleeps until ServerSilenceTimeout after the newest inbound read and re-checks. One timer
    // wait per wake-up, at most one per ServerSilenceTimeout while frames flow; nothing on the
    // receive path beyond the timestamp write.
    private async Task ServerSilenceWatchdogAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _joinAccepted.Task.WaitAsync(cancellationToken);
            while (IsConnected)
            {
                TimeSpan silence = _timeProvider.GetElapsedTime(Volatile.Read(ref _lastInboundTimestamp));
                if (silence >= ServerSilenceTimeout)
                {
                    EndSession(ServerSilenceDisconnectReason, "client.watchdog.silence");
                    return;
                }

                await Task.Delay(ServerSilenceTimeout - silence, _timeProvider, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    // Runs off the render thread. PeriodicTimer waits allocate nothing after the first.
    // Keepalives start only once the server's hello has shown it speaks this protocol version,
    // whose servers skip empty frames; a peer that never says hello gets none.
    private async Task KeepaliveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            ServerHelloMessage? hello;
            try
            {
                hello = await _serverHello.Task.WaitAsync(cancellationToken);
            }
            catch (ProtocolVersionMismatchException)
            {
                return;
            }

            if (hello == null)
            {
                return;
            }

            using PeriodicTimer timer = new(KeepaliveInterval / 2);
            while (IsConnected && await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (Stopwatch.GetElapsedTime(Volatile.Read(ref _lastSendTimestamp)) < KeepaliveInterval)
                {
                    continue;
                }

                // One signal per due keepalive: while a send is stalled the flag stays set (the
                // send loop clears it only after its current send), so the semaphore no longer
                // gains a release every half interval and the loop does not spin through them
                // once the send completes.
                if (Interlocked.Exchange(ref _keepaliveDue, 1) == 0)
                {
                    SignalSendLoop();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();
        // First, so it cannot start a send loop after the one below has been awaited.
        await StopLoopAsync(_keepaliveTask, "keepalive");
        await StopLoopAsync(_watchdogTask, "watchdog");
        // Before the send loop: a retry it queued must not start a send loop after that one ended.
        await StopLoopAsync(Volatile.Read(ref _joinSupervisorTask), "join-supervisor");
        await StopLoopAsync(_sendLoopTask, "send");
        await StopLoopAsync(_receiveLoopTask, "receive");

        // The send loop has exited, but a direct Send*Async call (SendHelloAsync and friends) does
        // not run on it and may still be writing the pooled frame buffer to the transport. Take
        // _sendLock so the buffer goes back to ArrayPool<byte>.Shared only after that send ends.
        // Senders that get the lock after this see _disposeState and throw ObjectDisposedException
        // instead of encoding into the returned buffer.
        //
        // Bounded like the loop waits above, and for the same reason. A send that is still holding
        // the lock when the bound expires keeps the pooled buffer: returning it to the pool under a
        // live send would hand that send's bytes to the next borrower, so the buffer is abandoned
        // (the GC collects it; the pool simply loses one rented array) rather than recycled.
        if (await _sendLock.WaitAsync(LoopShutdownTimeout))
        {
            try
            {
                _frameWriter.Dispose();
            }
            finally
            {
                _sendLock.Release();
            }
        }
        else
        {
            NetworkDiagnosticsLog.Write(
                "client.shutdown.timeout",
                $"endpoint={Options.EndpointLabel} session={Options.SessionId} send still in flight after {LoopShutdownTimeout.TotalSeconds:0} s: the frame buffer is not returned to the pool");
        }

        // _sendLock is deliberately not disposed: a sender still queued on WaitAsync would never be
        // woken by a disposed SemaphoreSlim. It owns no unmanaged handle unless AvailableWaitHandle
        // is read, which this class never does.
        _shutdown.Dispose();
        await _connection.DisposeAsync();
    }

    // Waits for one background loop to end after _shutdown was cancelled, for at most
    // LoopShutdownTimeout. The bound is what keeps the caller's thread free: PlayScene.Dispose joins
    // DisposeAsync synchronously on the render thread (PlayScene.Lifetime.DisposeRemoteClient), and
    // this process has no second thread that could close the window if that join never returned. A
    // loop that outstays the bound is abandoned, not killed; it runs on a thread-pool thread, its
    // cancellation is already requested, and the transport dispose at the end of DisposeAsync ends
    // the socket operation it may be parked in.
    private async ValueTask StopLoopAsync(Task? loop, string label)
    {
        if (loop == null)
        {
            return;
        }

        try
        {
            await loop.WaitAsync(LoopShutdownTimeout);
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException)
        {
            NetworkDiagnosticsLog.Write(
                "client.shutdown.timeout",
                $"endpoint={Options.EndpointLabel} session={Options.SessionId} loop={label} still running after {LoopShutdownTimeout.TotalSeconds:0} s: abandoning it");
        }
    }

    private async ValueTask SendMessageAsync(IClientToServerMessage message, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        Stopwatch? stopwatch = NetworkDiagnosticsLog.IsEnabled ? Stopwatch.StartNew() : null;
        int frameLength;
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            // Disposal may have started while this call waited for the lock; the frame writer is
            // then gone (or about to be), so fail cleanly rather than touch its buffer.
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
            ReadOnlyMemory<byte> frame = EncodeClientFrame(message);
            frameLength = frame.Length;
            // frame aliases _frameWriter's pooled buffer. Passing it to the transport is safe only
            // because the send is awaited here, inside _sendLock, before the next encode reuses the
            // buffer; transports must not keep the memory after SendAsync completes.
            await _connection.SendAsync(frame, cancellationToken);
            Volatile.Write(ref _lastSendTimestamp, Stopwatch.GetTimestamp());
        }
        finally
        {
            _sendLock.Release();
            stopwatch?.Stop();
        }

        LogOutbound(message, frameLength, stopwatch?.Elapsed.TotalMilliseconds ?? 0d);
    }

    // One empty frame: no envelope and no sequence number, so it cannot disturb the server's
    // ordering checks. Runs on the send loop only.
    private async ValueTask SendKeepaliveAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
            _frameWriter.BeginFrame(_framer);
            ReadOnlyMemory<byte> frame = _frameWriter.CompleteFrame(_framer);
            await _connection.SendAsync(frame, cancellationToken);
            Volatile.Write(ref _lastSendTimestamp, Stopwatch.GetTimestamp());
        }
        finally
        {
            _sendLock.Release();
        }

        long count = Interlocked.Increment(ref _sentKeepaliveCount);
        if (NetworkDiagnosticsLog.IsEnabled &&
            NetworkDiagnosticsLog.ShouldLogEvery(_keepaliveLogKey, TimeSpan.FromSeconds(5)))
        {
            NetworkDiagnosticsLog.Write(
                "client.tx.keepalive",
                $"endpoint={Options.EndpointLabel} session={Options.SessionId} count={count}");
        }
    }

    // Caller holds _sendLock.
    private ReadOnlyMemory<byte> EncodeClientFrame(IClientToServerMessage message)
    {
        TransportEnvelope envelope = new(
            TransportEnvelope.CurrentProtocolVersion,
            message.MessageKind,
            _nextOutboundSequence++);

        _frameWriter.BeginFrame(_framer);
        switch (message)
        {
            case ClientHelloMessage hello:
                _codec.Encode(envelope, hello, _frameWriter);
                break;
            case ClientJoinRequestMessage joinRequest:
                _codec.Encode(envelope, joinRequest, _frameWriter);
                break;
            case PlayerCommandMessage command:
                _codec.Encode(envelope, command, _frameWriter);
                break;
            case PlayerActionMessage action:
                _codec.Encode(envelope, action, _frameWriter);
                break;
            case ClientWorldResyncRequestMessage resync:
                _codec.Encode(envelope, resync, _frameWriter);
                break;
            case ClientChatMessage chat:
                _codec.Encode(envelope, chat, _frameWriter);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported client message type '{message.GetType().Name}' for remote client encoding.");
        }

        return _frameWriter.CompleteFrame(_framer);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await ProcessInboundOnceAsync(cancellationToken))
            {
            }

            // The server closed the socket: an idle, send-stall or backlog close, a kick or a
            // server shutdown. None of these is announced by a message.
            if (!cancellationToken.IsCancellationRequested)
            {
                TrySetDisconnectReason("Connection lost: the server closed the connection.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (WorldTransferLimitException ex)
        {
            SetDisconnectReasonOutright($"World transfer failed: {ex.Message}");
        }
        catch (ProtocolVersionMismatchException ex)
        {
            SetDisconnectReasonOutright(ex.Message);
            if (_serverHello.TrySetException(ex))
            {
                // Mark the fault observed: nobody awaits it once ConnectAsync has returned.
                _ = _serverHello.Task.Exception;
            }
        }
        catch (Exception ex)
        {
            // A malformed frame or a transport error; previously this faulted the loop task and
            // resurfaced from DisposeAsync.
            TrySetDisconnectReason($"Connection lost: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsConnected = false;
            _serverHello.TrySetResult(null);
            // The connection is gone; a join supervisor waiting on a reply stops without a reason
            // of its own.
            _joinReplies.Writer.TryComplete();
            NetworkDiagnosticsLog.Write(
                "client.disconnect",
                $"endpoint={Options.EndpointLabel} connection={_connection.ConnectionId} session={Options.SessionId} reason={DisconnectReason ?? "closed"}");
        }
    }

    private void LogInbound(TransportEnvelope envelope, IServerToClientMessage message)
    {
        if (!NetworkDiagnosticsLog.IsEnabled)
        {
            return;
        }

        string detail = message switch
        {
            JoinAcceptedMessage accepted => NetworkDiagnosticsLog.DescribeJoin(accepted),
            JoinRejectedMessage rejected => $"reason={rejected.Reason}",
            ServerSnapshotMessage snapshot => NetworkDiagnosticsLog.DescribeSnapshot(snapshot.Snapshot),
            ServerActionMessage action => $"session={action.Result.SessionId} actionSeq={action.Result.ActionSequence} accepted={action.Result.Accepted} payload={action.Result.Payload?.GetType().Name ?? "none"}",
            ServerHelloMessage hello => $"server={hello.ServerName} protocol={hello.ProtocolVersion} tick={hello.CurrentServerTick}",
            _ => string.Empty
        };

        string log = $"endpoint={Options.EndpointLabel} seq={envelope.Sequence} kind={message.MessageKind} queued={_inboundMessages.Count} {detail}";
        if (message is ServerSnapshotMessage)
        {
            NetworkDiagnosticsLog.WriteEvery(
                $"client.rx.snapshot.{Options.SessionId}",
                TimeSpan.FromSeconds(1),
                "client.rx",
                log);
            return;
        }

        NetworkDiagnosticsLog.Write("client.rx", log);
    }

    private void LogOutbound(IClientToServerMessage message, int byteCount, double elapsedMilliseconds)
    {
        if (!NetworkDiagnosticsLog.IsEnabled)
        {
            return;
        }

        if (message is PlayerCommandMessage)
        {
            // Commands are rate-limited; build the log line only when a limiter lets it through.
            bool logCommand = NetworkDiagnosticsLog.ShouldLogEvery(_commandLogKey, TimeSpan.FromSeconds(1));
            bool logSlow = elapsedMilliseconds > 4d
                && NetworkDiagnosticsLog.ShouldLogEvery(_slowCommandLogKey, TimeSpan.FromSeconds(1));
            if (!logCommand && !logSlow)
            {
                return;
            }

            string commandLog = DescribeOutbound(message, byteCount, elapsedMilliseconds);
            if (logCommand)
            {
                NetworkDiagnosticsLog.Write("client.tx", commandLog);
            }

            if (logSlow)
            {
                NetworkDiagnosticsLog.Write("client.tx.slow", commandLog);
            }

            return;
        }

        NetworkDiagnosticsLog.Write("client.tx", DescribeOutbound(message, byteCount, elapsedMilliseconds));
    }

    private string DescribeOutbound(IClientToServerMessage message, int byteCount, double elapsedMilliseconds)
    {
        string detail = message switch
        {
            ClientJoinRequestMessage join => $"session={join.RequestedSessionId} name={join.DisplayName}",
            PlayerCommandMessage command => $"session={command.CommandFrame.SessionId.Value} commandSeq={command.CommandFrame.Sequence}",
            PlayerActionMessage action => $"session={action.SessionId.Value} actionSeq={action.ActionSequence} payload={action.Payload.GetType().Name}",
            ClientHelloMessage hello => $"client={hello.ClientName}",
            _ => string.Empty
        };

        return $"endpoint={Options.EndpointLabel} kind={message.MessageKind} bytes={byteCount} sendMs={elapsedMilliseconds:0.00} {detail}";
    }
}
