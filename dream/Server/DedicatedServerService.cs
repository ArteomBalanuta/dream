using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Simulation.Persistence;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Server;


public sealed class DedicatedServerService : IAsyncDisposable
{
    // How long a rejection may take to reach a peer before its connection is closed anyway.
    private static readonly TimeSpan RejectionSendTimeout = TimeSpan.FromSeconds(1);

    // How long RunAsync and DisposeAsync wait for the connection loops to finish their cleanup
    // after closing every connection's transport.
    public static readonly TimeSpan ConnectionLoopShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly DedicatedServerHost _host;
    private readonly IServerTransport _transport;
    private readonly IProtocolCodec _codec;
    private readonly IMessageFramer _framer;
    private readonly ConcurrentDictionary<string, ServerConnection> _connections = new(StringComparer.Ordinal);
    // Every running RunConnectionLoopAsync task and its connection. A loop removes itself when it
    // completes. Shutdown closes the connections' transports and awaits these, so no loop can
    // call _host.ReleaseConnection after RunAsync or DisposeAsync has returned and the owner
    // disposes the host.
    private readonly ConcurrentDictionary<Task, ServerConnection> _connectionLoops = new();
    // 0 while serving, 1 once shutdown has started; connection loops ending after that are not
    // reported as errors.
    private int _stopping;
    // Connections the service closed because a rejoin took over their session; their loops end on
    // the closed transport, which is not reported as an error. A loop removes its own entry.
    private readonly ConcurrentDictionary<ServerConnection, byte> _takenOverConnections = new();

    // Tick-loop scratch. The connection list mirrors _connections and is rebuilt only after a
    // connection is attached or removed.
    private readonly List<ServerConnection> _connectionList = [];
    private readonly List<ServerConnection> _joinedConnections = [];
    private readonly List<AuthoritativeActionResult> _pendingActionResults = [];
    private readonly List<PendingConnectionReply> _pendingJoinReplies = [];
    private int _connectionsChanged = 1;
    private bool _started;
    // Ticks run since the last broadcast snapshot (tick thread only). It keeps counting while
    // nobody is joined and starts "due", so the first joined advance broadcasts at once.
    private int _ticksSinceSnapshot = SnapshotDueTicks;
    private long _snapshotsBroadcast;
    private long _skippedSnapshotAdvances;
    private long _oversizedBroadcasts;

    // Always-on tick metrics. Dropped ticks are read from the host's tick runner after each advance.
    private readonly ServerTickMetrics _metrics = new();
    private readonly BackgroundCheckpointWriter? _checkpointWrites;
    private readonly PersistenceWriteQueue? _persistenceWrites;
    // Counters of connections already removed, so service totals survive disconnects.
    private long _closedBytesSent;
    private long _closedFramesSent;
    private long _closedBytesReceived;
    private long _closedFramesReceived;
    private long _closedCoalescedSnapshots;
    private long _closedBackToBackWorldTransfers;
    // Held while a connection is removed and its counters retired, and while CaptureMetrics sums.
    private readonly object _connectionTotalsSync = new();
    private readonly object _performanceSync = new();
    private DedicatedServerPerformanceSnapshot _latestPerformance = DedicatedServerPerformanceSnapshot.Empty;
    private long _latestPerformanceVersion;

    public DedicatedServerService(
        DedicatedServerHost host,
        IServerTransport transport,
        IProtocolCodec codec,
        IMessageFramer framer)
        : this(host, transport, codec, framer, ServerConnectionOptions.Default)
    {
    }

    // connectionOptions apply to every connection this service accepts. A connection attached
    // with AttachConnection keeps the options it was built with.
    public DedicatedServerService(
        DedicatedServerHost host,
        IServerTransport transport,
        IProtocolCodec codec,
        IMessageFramer framer,
        ServerConnectionOptions connectionOptions)
    {
        ArgumentNullException.ThrowIfNull(connectionOptions);
        _host = host;
        _transport = transport;
        _codec = codec;
        _framer = framer;
        ConnectionOptions = connectionOptions;
        SnapshotIntervalTicks = Math.Clamp(connectionOptions.SnapshotIntervalTicks, 1, SnapshotDueTicks);
        _checkpointWrites = host.CheckpointWrites;
        _persistenceWrites = host.Runtime?.PersistenceWrites;
        // Stale-session takeover measures silence on the same clock as the idle timeout.
        host.TimeProvider = connectionOptions.TimeProvider;
    }

    public int Port => _transport.Port;

    public int ConnectionCount => _connections.Count;

    public ServerConnectionOptions ConnectionOptions { get; }

    // The largest snapshot interval; _ticksSinceSnapshot starts and saturates here, so it is always due.
    private const int SnapshotDueTicks = int.MaxValue / 2;

    // ConnectionOptions.SnapshotIntervalTicks, clamped to 1 .. int.MaxValue / 2.
    public int SnapshotIntervalTicks { get; }

    // Snapshots broadcast and advances whose snapshot was held back by SnapshotIntervalTicks.
    public long SnapshotsBroadcast => Interlocked.Read(ref _snapshotsBroadcast);

    public long SkippedSnapshotAdvances => Interlocked.Read(ref _skippedSnapshotAdvances);

    // Broadcasts sent without their deltas because one advance's deltas passed the merge caps
    // (every joined connection was flagged for a world resync instead).
    public long OversizedBroadcasts => Interlocked.Read(ref _oversizedBroadcasts);

    public ServerTickMetrics TickMetrics => _metrics;

    // The last AdvanceOnceAsync call's phase times. Built on read (at most once per advance), so
    // the tick itself allocates nothing for it.
    public DedicatedServerPerformanceSnapshot LatestPerformance
    {
        get
        {
            long version = _metrics.Version;
            lock (_performanceSync)
            {
                if (version != _latestPerformanceVersion)
                {
                    _metrics.GetLastPhases(out double advanceMs, out double snapshotMs, out double broadcastMs);
                    _latestPerformance = new DedicatedServerPerformanceSnapshot(advanceMs, snapshotMs, broadcastMs, Sampled: true);
                    _latestPerformanceVersion = version;
                }

                return _latestPerformance;
            }
        }
    }

    // Copies every server metric. Safe from any thread; allocation-free apart from enumerating the
    // connection dictionary. resetWindow restarts the windowed tick histogram.
    public ServerMetricsSnapshot CaptureMetrics(bool resetWindow = false)
    {
        ServerTickMetricsSnapshot tick = _metrics.Capture(resetWindow);
        long bytesSent;
        long framesSent;
        long bytesReceived;
        long framesReceived;
        long coalesced;
        long backToBackTransfers;
        int connections = 0;
        int joined = 0;
        lock (_connectionTotalsSync)
        {
            bytesSent = Interlocked.Read(ref _closedBytesSent);
            framesSent = Interlocked.Read(ref _closedFramesSent);
            bytesReceived = Interlocked.Read(ref _closedBytesReceived);
            framesReceived = Interlocked.Read(ref _closedFramesReceived);
            coalesced = Interlocked.Read(ref _closedCoalescedSnapshots);
            backToBackTransfers = Interlocked.Read(ref _closedBackToBackWorldTransfers);
            foreach (KeyValuePair<string, ServerConnection> entry in _connections)
            {
                ServerConnection connection = entry.Value;
                connections++;
                if (_host.IsConnectionJoined(connection.ConnectionId))
                {
                    joined++;
                }

                bytesSent += connection.BytesSent;
                framesSent += connection.FramesSent;
                bytesReceived += connection.BytesReceived;
                framesReceived += connection.FramesReceived;
                coalesced += connection.CoalescedSnapshots;
                backToBackTransfers += connection.BackToBackWorldTransfers;
            }
        }

        return new ServerMetricsSnapshot(
            tick,
            connections,
            joined,
            bytesSent,
            framesSent,
            bytesReceived,
            framesReceived,
            coalesced,
            _checkpointWrites?.CompletedWrites ?? 0,
            _checkpointWrites?.FailedWrites ?? 0,
            _checkpointWrites?.SupersededCheckpoints ?? 0,
            _checkpointWrites?.LastWriteMilliseconds ?? 0d,
            _checkpointWrites?.MaxWriteMilliseconds ?? 0d,
            _checkpointWrites?.MeanWriteMilliseconds ?? 0d,
            _persistenceWrites?.CompletedWrites ?? 0,
            _persistenceWrites?.FailedWrites ?? 0,
            _metrics.IdleTimeoutCloses,
            _metrics.SendStallCloses,
            _metrics.BacklogOverflowCloses,
            _metrics.CoalesceOverflowResyncs,
            _metrics.CoalesceOverflowResyncsDeferred,
            _metrics.BackToBackOverflowResyncs,
            backToBackTransfers);
    }

    // Raised once for each connection closed by an error, after the error is logged. The handler
    // runs on the connection's receive task and must not block.
    public event Action<ServerConnectionError>? ConnectionError;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _transport.Start();
        _started = true;
        NetworkDiagnosticsLog.Write("server.start", $"port={Port}");
    }

    public void AttachConnection(ServerConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _host.ForgetTakenOverConnection(connection.ConnectionId);
        _connections[connection.ConnectionId] = connection;
        Volatile.Write(ref _connectionsChanged, 1);
    }

    public async ValueTask<ServerConnection> AcceptNextClientAsync(CancellationToken cancellationToken = default)
    {
        ITransportConnection accepted = await _transport.AcceptClientAsync(cancellationToken);
        while (ConnectionOptions.MaxConnections > 0 && _connections.Count >= ConnectionOptions.MaxConnections)
        {
            // Over the cap: closed right after accept, before it gets a connection, loop or context.
            NetworkDiagnosticsLog.Write(
                "server.accept.refused",
                $"connection={accepted.ConnectionId} connections={_connections.Count} max={ConnectionOptions.MaxConnections}");
            try
            {
                await RefuseOverCapAsync(accepted);
            }
            catch (Exception ex)
            {
                NetworkDiagnosticsLog.Write(
                    "server.connection.close-failed",
                    $"connection={accepted.ConnectionId} error={ex.GetType().Name}: {ex.Message}");
            }

            accepted = await _transport.AcceptClientAsync(cancellationToken);
        }

        ServerConnection connection = new(accepted, _host, _codec, _framer, ConnectionOptions);
        AttachConnection(connection);
        NetworkDiagnosticsLog.Write(
            "server.accept",
            $"connection={connection.ConnectionId} connections={_connections.Count}");
        TrackConnectionLoop(connection, RunConnectionLoopAsync(connection, cancellationToken));
        return connection;
    }

    // One best-effort JoinRejected before the close, so the client can say why. Bounded, and the
    // connection is never attached, so it holds no slot and no host context.
    private async ValueTask RefuseOverCapAsync(ITransportConnection accepted)
    {
        ServerConnection refused = new(accepted, _host, _codec, _framer, ConnectionOptions);
        await TrySendRejectionAsync(refused, ServerConnectionOptions.ServerFullRejectionReason);
        await refused.DisposeAsync();
    }

    // Waits until every connection has written its queued messages (tests and shutdown).
    public async ValueTask<bool> FlushOutboundAsync(TimeSpan timeout)
    {
        bool flushed = true;
        foreach (ServerConnection connection in _connections.Values)
        {
            flushed &= await connection.FlushOutboundAsync(timeout);
        }

        return flushed;
    }

    public async ValueTask<int> AdvanceOnceAsync(double elapsedSeconds, CancellationToken cancellationToken = default)
    {
        await DropFaultedConnectionsAsync();
        return AdvanceAndBroadcast(elapsedSeconds);
    }

    // The synchronous body of AdvanceOnceAsync. Kept out of the async method so its locals (the
    // phase timestamps) do not widen the async state machine that Debug builds allocate per call.
    private int AdvanceAndBroadcast(double elapsedSeconds)
    {
        long checkpointsBefore = _checkpointWrites?.EnqueuedCheckpoints ?? 0;
        long advanceStart = ServerTickMetrics.Timestamp();
        int executedTicks = _host.AdvanceTime(elapsedSeconds);
        long advanceTicks = ServerTickMetrics.Timestamp() - advanceStart;
        bool capturedCheckpoint = (_checkpointWrites?.EnqueuedCheckpoints ?? 0) != checkpointsBefore;
        if (executedTicks > 0)
        {
            // Saturates at the largest interval, so it never overflows and stays due once due.
            _ticksSinceSnapshot = (int)Math.Min((long)_ticksSinceSnapshot + executedTicks, SnapshotDueTicks);
        }

        DeliverPendingJoinReplies();
        if (executedTicks <= 0 || _connections.IsEmpty)
        {
            if (executedTicks > 0)
            {
                _host.DiscardUndeliveredSnapshotDeltas();
            }

            RecordAdvance(executedTicks, advanceTicks, snapshotTicks: 0, broadcastTicks: 0, capturedCheckpoint);
            return executedTicks;
        }

        RefreshConnectionList();
        List<ServerConnection> joinedConnections = _joinedConnections;
        joinedConnections.Clear();
        for (int i = 0; i < _connectionList.Count; i++)
        {
            if (_host.IsConnectionJoined(_connectionList[i].ConnectionId))
            {
                joinedConnections.Add(_connectionList[i]);
            }
        }

        if (joinedConnections.Count == 0)
        {
            _host.DiscardUndeliveredSnapshotDeltas();
            RecordAdvance(executedTicks, advanceTicks, snapshotTicks: 0, broadcastTicks: 0, capturedCheckpoint);
            if (NetworkDiagnosticsLog.IsEnabled)
            {
                LogUnjoinedTick(executedTicks, ServerTickMetrics.ToMilliseconds(advanceTicks));
            }

            return executedTicks;
        }

        // Snapshot interval: the ticks run since the last snapshot stay pending in the runtime
        // (world mutations, damage events, accepted command sequences) and ride in the next one,
        // and their action results stay queued in the host until after it, so a skipped advance
        // loses nothing. Join replies and world resyncs above are never held back.
        if (_ticksSinceSnapshot < SnapshotIntervalTicks)
        {
            Interlocked.Increment(ref _skippedSnapshotAdvances);
            RecordAdvance(executedTicks, advanceTicks, snapshotTicks: 0, broadcastTicks: 0, capturedCheckpoint);
            return executedTicks;
        }

        _ticksSinceSnapshot = 0;
        Interlocked.Increment(ref _snapshotsBroadcast);
        long snapshotStart = ServerTickMetrics.Timestamp();
        ServerSnapshotMessage snapshot = _host.CreateSnapshotMessage();
        // One advance runs up to DefaultMaxTicksPerAdvance ticks and nothing caps the fractures or
        // damage of one tick, so a broadcast can carry more deltas than the merge caps allow a
        // lagging connection, and its shared payload can pass the frame cap, which would fault
        // every connection's writer in the same tick. Past the caps the deltas are dropped and
        // every joined connection gets a world resync on the coalesce-overflow path (deferred
        // behind a transfer still going out). The writer catches a payload that passes the frame
        // cap under the caps the same way (ServerConnection.EncodeServerFrame).
        bool dropDeltas = ExceedsDeltaCaps(snapshot.Snapshot, ConnectionOptions);
        if (dropDeltas)
        {
            if (NetworkDiagnosticsLog.IsEnabled)
            {
                NetworkDiagnosticsLog.Write(
                    "server.snapshot.oversized",
                    $"tick={snapshot.Snapshot.ServerTick} mutations={snapshot.Snapshot.WorldMutations?.Count ?? 0} " +
                    $"damage={snapshot.Snapshot.DamageEvents?.Count ?? 0} connections={joinedConnections.Count}; deltas dropped, world resyncs requested");
            }

            snapshot = ServerConnection.WithoutDeltas(snapshot);
            Interlocked.Increment(ref _oversizedBroadcasts);
        }

        long broadcastStart = ServerTickMetrics.Timestamp();
        // Enqueue only: each connection's writer task does the socket I/O, so a stalled client
        // cannot hold up the tick loop for everyone else. The snapshot payload is the same for
        // every joined connection (it carries every session's accepted command sequence), so it
        // is serialized once, by the first writer that needs it, and each connection adds only
        // its own envelope.
        SharedEncodedPayload sharedPayload = new(_codec, snapshot);
        for (int i = 0; i < joinedConnections.Count; i++)
        {
            ServerConnection connection = joinedConnections[i];
            connection.EnqueueSnapshot(snapshot, sharedPayload);
            if (dropDeltas)
            {
                connection.FlagOverflowResync();
            }

            if (connection.TryTakeOverflowResync())
            {
                RequestOverflowResync(connection);
            }
        }

        long broadcastEnd = ServerTickMetrics.Timestamp();
        RecordAdvance(
            executedTicks,
            advanceTicks,
            broadcastStart - snapshotStart,
            broadcastEnd - broadcastStart,
            capturedCheckpoint);

        // Only the results are handed to connections, never this list, so it can be reused.
        List<AuthoritativeActionResult> pendingActionResults = _pendingActionResults;
        _host.DrainPendingActionResultsInto(pendingActionResults);
        for (int resultIndex = 0; resultIndex < pendingActionResults.Count; resultIndex++)
        {
            AuthoritativeActionResult actionResult = pendingActionResults[resultIndex];
            for (int i = 0; i < joinedConnections.Count; i++)
            {
                ServerConnection connection = joinedConnections[i];
                if (_host.TryGetConnectionContext(connection.ConnectionId, out ServerRoutingContext? context)
                    && context?.LastJoinRequest?.RequestedSessionId == actionResult.SessionId)
                {
                    connection.Enqueue(new ServerActionMessage(actionResult));
                }
            }
        }

        if (NetworkDiagnosticsLog.IsEnabled)
        {
            LogJoinedTick(
                executedTicks,
                elapsedSeconds,
                joinedConnections.Count,
                pendingActionResults.Count,
                ServerTickMetrics.ToMilliseconds(advanceTicks),
                ServerTickMetrics.ToMilliseconds(broadcastStart - snapshotStart),
                ServerTickMetrics.ToMilliseconds(broadcastEnd - broadcastStart),
                snapshot);
        }

        pendingActionResults.Clear();
        return executedTicks;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        Start();

        Task acceptLoop = AcceptLoopAsync(cancellationToken);
        Task tickLoop = TickLoopAsync(cancellationToken);

        try
        {
            await Task.WhenAll(acceptLoop, tickLoop);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            // The tick loop has exited; the connection loops may still be releasing their
            // seats. Callers dispose the host right after RunAsync returns, so wait for them.
            await StopConnectionLoopsAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopConnectionLoopsAsync();
        foreach (ServerConnection connection in _connections.Values)
        {
            await connection.DisposeAsync();
        }

        _connections.Clear();
        Volatile.Write(ref _connectionsChanged, 1);
        await _transport.DisposeAsync();
    }

    // Number of connection loops still running; tests use it to check shutdown.
    public int RunningConnectionLoopCount => _connectionLoops.Count;

    private void TrackConnectionLoop(ServerConnection connection, Task loop)
    {
        _connectionLoops[loop] = connection;
        // Registered after the add, so the removal can never run before it.
        _ = loop.ContinueWith(
            static (completed, state) => ((ConcurrentDictionary<Task, ServerConnection>)state!).TryRemove(completed, out _),
            _connectionLoops,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    // Closes the transport of every connection that still has a loop running, which ends its
    // pending receive, then waits (bounded) for the loops, whose cleanup releases each seat in
    // the host. Idempotent; safe to call from RunAsync and again from DisposeAsync.
    private async ValueTask StopConnectionLoopsAsync()
    {
        Volatile.Write(ref _stopping, 1);
        if (_connectionLoops.IsEmpty)
        {
            return;
        }

        List<Task> loops = new(_connectionLoops.Count);
        foreach (KeyValuePair<Task, ServerConnection> entry in _connectionLoops)
        {
            loops.Add(entry.Key);
            try
            {
                await entry.Value.CloseTransportAsync();
            }
            catch (Exception ex)
            {
                NetworkDiagnosticsLog.Write(
                    "server.connection.close-failed",
                    $"connection={entry.Value.ConnectionId} error={ex.GetType().Name}: {ex.Message}");
            }
        }

        try
        {
            // The loops never fault (RunConnectionLoopAsync catches everything).
            await Task.WhenAll(loops).WaitAsync(ConnectionLoopShutdownTimeout);
        }
        catch (TimeoutException)
        {
            string line = $"connection loops still running after {ConnectionLoopShutdownTimeout.TotalSeconds:0} s: {_connectionLoops.Count}";
            NetworkDiagnosticsLog.Write("server.shutdown.timeout", line);
            Console.Error.WriteLine($"[server.shutdown.timeout] {line}");
        }
        catch (Exception ex)
        {
            // Not expected: RunConnectionLoopAsync catches everything. Shutdown goes on.
            NetworkDiagnosticsLog.Write("server.shutdown.loop-error", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await AcceptNextClientAsync(cancellationToken);
        }
    }

    // Runs on a dedicated thread and waits for each tick deadline with TickPacer, so most loop
    // iterations execute exactly one tick and send one snapshot.
    private Task TickLoopAsync(CancellationToken cancellationToken)
    {
        return Task.Factory.StartNew(
            () =>
            {
                using TickPacer pacer = new();
                Stopwatch stopwatch = Stopwatch.StartNew();
                while (!cancellationToken.IsCancellationRequested)
                {
                    double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
                    stopwatch.Restart();
                    AdvanceOnceAsync(elapsedSeconds, cancellationToken).AsTask().GetAwaiter().GetResult();
                    pacer.WaitFor(_host.SecondsUntilNextTick, cancellationToken);
                }
            },
            cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    // A host that defers joins applies them at the start of a tick and leaves the JoinAccepted or
    // JoinRejected to be sent here. They are queued before this tick's snapshot, so a client hears
    // its join reply first. A reply for a connection that has since closed is dropped.
    private void DeliverPendingJoinReplies()
    {
        List<PendingConnectionReply> replies = _pendingJoinReplies;
        _host.DrainPendingJoinRepliesInto(replies);
        for (int i = 0; i < replies.Count; i++)
        {
            PendingConnectionReply reply = replies[i];
            if (_connections.TryGetValue(reply.ConnectionId, out ServerConnection? connection))
            {
                connection.Enqueue(reply.Message);
            }
        }

        replies.Clear();
    }

    // Also runs each connection's liveness check, so a peer that went silent (idle timeout) or
    // stopped reading (send stall) is faulted here and released through the same path as any
    // other faulted connection. A faulted connection closes its transport before waiting for its
    // writer, so a stalled socket write cannot hold up the tick.
    //
    // It first closes the connections whose session a rejoin took over (NETR-8; the host already
    // released them), and reports every connection's received-frame count to the host, so the
    // client's empty keepalive frames count as liveness for the takeover check too.
    private async ValueTask DropFaultedConnectionsAsync()
    {
        while (_host.TryTakeTakenOverConnection(out string takenOverId))
        {
            StartClosingTakenOverConnection(takenOverId);
        }

        RefreshConnectionList();
        for (int i = 0; i < _connectionList.Count; i++)
        {
            ServerConnection connection = _connectionList[i];
            _host.ObserveInboundFrames(connection.ConnectionId, connection.FramesReceived);
            if (!connection.CheckLiveness(_host.IsConnectionJoined(connection.ConnectionId)) || !TryRemoveConnection(connection))
            {
                continue;
            }

            _metrics.RecordConnectionClosed(connection.CloseReason);
            _host.ReleaseConnection(connection.ConnectionId);
            NetworkDiagnosticsLog.Write(
                "server.disconnect",
                $"connection={connection.ConnectionId} reason=faulted kind={connection.CloseReason} connections={_connections.Count}");
            await connection.DisposeAsync();
        }
    }

    // A connection whose session a rejoin took over: it was silent past the takeover window, so
    // its transport is closed before its writer is awaited (a dead peer may hold a socket write).
    // The host already released it, so its release here finds nothing left. A connection the
    // service already removed (its loop ended, or a liveness fault) is left to that path.
    private static readonly TimeSpan TakeoverNoticeFlushTimeout = TimeSpan.FromMilliseconds(250);

    // The removal runs on the tick; the notice, flush, close, release and dispose run on a tracked
    // background task, so a dead peer's flush cannot hold the tick and shutdown still waits for it.
    private void StartClosingTakenOverConnection(string connectionId)
    {
        if (!_connections.TryGetValue(connectionId, out ServerConnection? connection)
            || !TryRemoveConnection(connection))
        {
            return;
        }

        _takenOverConnections[connection] = 0;
        TrackConnectionLoop(connection, Task.Run(() => CloseTakenOverConnectionAsync(connection)));
    }

    private async Task CloseTakenOverConnectionAsync(ServerConnection connection)
    {
        string connectionId = connection.ConnectionId;
        try
        {
            // The old client hears why before its socket closes. Bounded, so a dead peer cannot hold this task.
            connection.Enqueue(new JoinRejectedMessage(_host.TakeCloseReason(connectionId)));
            await connection.FlushOutboundAsync(TakeoverNoticeFlushTimeout);
            await connection.CloseTransportAsync();
        }
        catch (Exception ex)
        {
            NetworkDiagnosticsLog.Write(
                "server.connection.close-failed",
                $"connection={connectionId} error={ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            _host.ReleaseConnection(connectionId);
            NetworkDiagnosticsLog.Write(
                "server.disconnect",
                $"connection={connectionId} reason=session-taken-over connections={_connections.Count}");
            await connection.DisposeAsync();
        }
        catch (Exception ex)
        {
            ReportConnectionError(connection, "cleanup-error", $"{ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    // Same caps as a lagging connection's merge (ServerConnection.AppendCoalesced). Allocation-free.
    internal static bool ExceedsDeltaCaps(in ServerSnapshotEnvelope snapshot, ServerConnectionOptions options)
    {
        return (snapshot.WorldMutations?.Count ?? 0) > options.MaxCoalescedWorldMutations ||
            (snapshot.DamageEvents?.Count ?? 0) > options.MaxCoalescedDamageEvents;
    }

    // A lagging connection's merged deltas passed the cap and were dropped. Ask for a full world
    // through the same path a client's checksum-mismatch request takes (WorldResyncRequestHandler
    // marks it, the host serves it after the next tick, subject to the per-connection cooldown);
    // if the cooldown refuses it, the flag is re-armed and the next tick asks again. Only an
    // accepted resync is counted. The refused path runs every tick until the cooldown passes, so
    // it must not allocate.
    // While the connection still has a world transfer queued or going out, the overflow is
    // deferred (re-armed) instead: a resync served now would queue a second full world behind the
    // first, and deltas dropped meanwhile are covered by the one resync served once the transfer
    // has finished. Each wait is counted once (CoalesceOverflowResyncsDeferred), and a resync
    // served after one is counted as back to back (BackToBackOverflowResyncs).
    private void RequestOverflowResync(ServerConnection connection)
    {
        try
        {
            if (connection.HasWorldTransferPending)
            {
                if (connection.DeferOverflowResync())
                {
                    _metrics.RecordCoalesceOverflowResyncDeferred();
                }

                return;
            }

            // Never recreates a context for a connection the host already released.
            bool accepted = _host.RequestWorldResyncIfConnected(connection.ConnectionId);
            if (accepted)
            {
                _metrics.RecordCoalesceOverflowResync();
                if (connection.TakeOverflowResyncDeferred())
                {
                    _metrics.RecordBackToBackOverflowResync();
                }
            }

            HandleOverflowResyncResult(connection, accepted);
        }
        catch (Exception ex)
        {
            NetworkDiagnosticsLog.Write(
                "server.connection.resync-failed",
                $"connection={connection.ConnectionId} error={ex.GetType().Name}: {ex.Message}");
        }
    }

    // The host's answer to an overflow resync request (DedicatedServerHost.RequestWorldResyncIfConnected).
    // A refused request (the cooldown) is re-armed on a connection that is still live, so a later
    // tick retries it; nothing is re-armed for a faulted or removed connection. Returns whether it
    // re-armed.
    internal bool HandleOverflowResyncResult(ServerConnection connection, bool accepted)
    {
        if (accepted ||
            connection.IsFaulted ||
            !_connections.TryGetValue(connection.ConnectionId, out ServerConnection? live) ||
            !ReferenceEquals(live, connection))
        {
            return false;
        }

        connection.RearmOverflowResync();
        return true;
    }

    // Started by AcceptNextClientAsync and tracked in _connectionLoops; it must never end
    // faulted: every error is logged once and closes only this connection.
    private async Task RunConnectionLoopAsync(ServerConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            while (await connection.ProcessIncomingAsync(cancellationToken))
            {
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (ProtocolVersionMismatchException ex)
        {
            ReportConnectionError(connection, "protocol-mismatch", ex.Message, ex);
            await TrySendRejectionAsync(connection, ex.Message);
        }
        catch (Exception) when (connection.CloseReason != ServerConnectionCloseReason.None)
        {
            // The connection faulted first (liveness, backlog overflow or a writer error) and its
            // transport was closed, which aborts the pending receive; the fault was already
            // logged and counted.
        }
        catch (Exception) when (Volatile.Read(ref _stopping) != 0)
        {
            // Shutdown closed the transport under the pending receive.
        }
        catch (Exception) when (_takenOverConnections.ContainsKey(connection))
        {
            // A rejoin took over this connection's session and the service closed its transport.
        }
        catch (Exception ex) when (IsPeerReset(ex))
        {
            // The client closed its socket with bytes still unread (Windows then sends an RST
            // instead of a FIN): a normal disconnect, not a connection error.
            NetworkDiagnosticsLog.Write(
                "server.connection.reset",
                $"connection={connection.ConnectionId} reason={ex.GetType().Name}: {ex.Message}");
        }
        catch (Exception ex)
        {
            ReportConnectionError(connection, "error", $"{ex.GetType().Name}: {ex.Message}", ex);
        }
        finally
        {
            _takenOverConnections.TryRemove(connection, out _);
            try
            {
                if (TryRemoveConnection(connection))
                {
                    if (connection.IsFaulted)
                    {
                        _metrics.RecordConnectionClosed(connection.CloseReason);
                    }

                    _host.ReleaseConnection(connection.ConnectionId);
                    NetworkDiagnosticsLog.Write(
                        "server.disconnect",
                        $"connection={connection.ConnectionId} connections={_connections.Count}");
                    await connection.DisposeAsync();
                }
            }
            catch (Exception ex)
            {
                ReportConnectionError(connection, "cleanup-error", $"{ex.GetType().Name}: {ex.Message}", ex);
            }

            // The receive loop has ended, so no late message from this connection can reach the
            // host any more; a takeover mark for it (NETR-8) has done its job. Without this the
            // host's set of taken-over ids only shrank when a new socket reused the same id.
            _host.ForgetTakenOverConnection(connection.ConnectionId);
        }
    }

    // True for a receive that failed because the peer reset or aborted the connection: a
    // SocketException with ConnectionReset or ConnectionAborted, as such or as the inner exception
    // of the IOException NetworkStream wraps it in.
    internal static bool IsPeerReset(Exception exception)
    {
        SocketException? socketError = exception as SocketException ?? (exception as IOException)?.InnerException as SocketException;
        return socketError is { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted };
    }

    // A version mismatch is answered with one JoinRejectedMessage in the current envelope before
    // the close. A peer on another version cannot decode it as a message, but every version reads
    // the envelope's top-level protocolVersion, the reason is readable in the raw frame, and an
    // older client's own decode error then names this server's version instead of a bare EOF.
    // A future client that reads the envelope leniently can show the reason directly.
    private async ValueTask TrySendRejectionAsync(ServerConnection connection, string reason)
    {
        using CancellationTokenSource timeout = new(RejectionSendTimeout);
        try
        {
            await connection.SendJoinRejectedAsync(new JoinRejectedMessage(reason), timeout.Token);
        }
        catch (Exception ex)
        {
            NetworkDiagnosticsLog.Write(
                "server.connection.reject-failed",
                $"connection={connection.ConnectionId} error={ex.GetType().Name}: {ex.Message}");
        }
    }

    // The connection id of a TCP connection is its remote endpoint. The line goes to the network
    // diagnostics log when it is on and to stderr otherwise, so an operator always sees it once.
    private void ReportConnectionError(ServerConnection connection, string kind, string reason, Exception exception)
    {
        string line = $"connection={connection.ConnectionId} kind={kind} reason={reason}";
        if (NetworkDiagnosticsLog.IsEnabled)
        {
            NetworkDiagnosticsLog.Write("server.connection.error", line);
        }
        else
        {
            Console.Error.WriteLine($"[server.connection.error] {line}");
        }

        try
        {
            ConnectionError?.Invoke(new ServerConnectionError(connection.ConnectionId, kind, reason, exception));
        }
        catch (Exception handlerError)
        {
            Console.Error.WriteLine($"[server.connection.error] handler failed: {handlerError.GetType().Name}: {handlerError.Message}");
        }
    }

    // Enumerating the ConcurrentDictionary is safe while connections come and go; a change made
    // mid-enumeration sets the flag again, so the next call rebuilds.
    private void RefreshConnectionList()
    {
        if (Interlocked.Exchange(ref _connectionsChanged, 0) == 0)
        {
            return;
        }

        _connectionList.Clear();
        foreach (KeyValuePair<string, ServerConnection> connection in _connections)
        {
            _connectionList.Add(connection.Value);
        }
    }

    private void RecordAdvance(
        int executedTicks,
        long advanceTicks,
        long snapshotTicks,
        long broadcastTicks,
        bool capturedCheckpoint)
    {
        // On the tick thread, after AdvanceTime: the runner's own total, so nothing can drift.
        _metrics.RecordAdvance(
            executedTicks,
            advanceTicks,
            snapshotTicks,
            broadcastTicks,
            capturedCheckpoint,
            _host.DroppedTicks,
            _persistenceWrites?.PendingCount ?? -1);
    }

    // Removes a connection and moves its traffic into the closed totals in one step under
    // _connectionTotalsSync, which CaptureMetrics also holds, so a capture sees the connection
    // either live or retired, never in neither. False when it was already removed.
    private bool TryRemoveConnection(ServerConnection connection)
    {
        lock (_connectionTotalsSync)
        {
            if (!_connections.TryRemove(connection.ConnectionId, out _))
            {
                return false;
            }

            RetireConnectionCounters(connection);
        }

        Volatile.Write(ref _connectionsChanged, 1);
        return true;
    }

    // Keeps a removed connection's traffic in the service totals.
    private void RetireConnectionCounters(ServerConnection connection)
    {
        Interlocked.Add(ref _closedBytesSent, connection.BytesSent);
        Interlocked.Add(ref _closedFramesSent, connection.FramesSent);
        Interlocked.Add(ref _closedBytesReceived, connection.BytesReceived);
        Interlocked.Add(ref _closedFramesReceived, connection.FramesReceived);
        Interlocked.Add(ref _closedCoalescedSnapshots, connection.CoalescedSnapshots);
        Interlocked.Add(ref _closedBackToBackWorldTransfers, connection.BackToBackWorldTransfers);
    }

    private void LogUnjoinedTick(int executedTicks, double advanceMs)
    {
        NetworkDiagnosticsLog.WriteEvery(
            "server.tick.unjoined",
            TimeSpan.FromSeconds(1),
            "server.tick",
            () => $"ticks={executedTicks} connections={_connections.Count} joined=0 advanceMs={advanceMs:0.00}");
    }

    private void LogJoinedTick(
        int executedTicks,
        double elapsedSeconds,
        int joinedCount,
        int actionCount,
        double advanceMs,
        double snapshotMs,
        double broadcastMs,
        ServerSnapshotMessage snapshot)
    {
        NetworkDiagnosticsLog.WriteEvery(
            "server.tick.joined",
            TimeSpan.FromSeconds(1),
            "server.tick",
            () => $"ticks={executedTicks} elapsed={elapsedSeconds * 1000d:0.00}ms connections={_connections.Count} " +
                $"joined={joinedCount} advanceMs={advanceMs:0.00} " +
                $"snapshotMs={snapshotMs:0.00} " +
                $"broadcastMs={broadcastMs:0.00} actions={actionCount} " +
                NetworkDiagnosticsLog.DescribeSnapshot(snapshot.Snapshot));
    }
}

// Why the service closed a connection: kind is "protocol-mismatch", "error" or "cleanup-error";
// reason is the logged text.
public sealed record ServerConnectionError(string ConnectionId, string Kind, string Reason, Exception Exception);
