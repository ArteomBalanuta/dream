using System.Collections.Concurrent;
using System.Diagnostics;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Server.Persistence;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Server;


public sealed class DedicatedServerService : IAsyncDisposable
{
    // How long a rejection may take to reach a peer before its connection is closed anyway.
    private static readonly TimeSpan RejectionSendTimeout = TimeSpan.FromSeconds(1);

    private readonly DedicatedServerHost _host;
    private readonly IServerTransport _transport;
    private readonly IProtocolCodec _codec;
    private readonly IMessageFramer _framer;
    private readonly ConcurrentDictionary<string, ServerConnection> _connections = new(StringComparer.Ordinal);

    // Tick-loop scratch. The connection list mirrors _connections and is rebuilt only after a
    // connection is attached or removed.
    private readonly List<ServerConnection> _connectionList = [];
    private readonly List<ServerConnection> _joinedConnections = [];
    private readonly List<AuthoritativeActionResult> _pendingActionResults = [];
    private readonly List<PendingConnectionReply> _pendingJoinReplies = [];
    private int _connectionsChanged = 1;
    private bool _started;

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
    {
        _host = host;
        _transport = transport;
        _codec = codec;
        _framer = framer;
        _checkpointWrites = host.CheckpointWrites;
        _persistenceWrites = host.Runtime?.PersistenceWrites;
    }

    public int Port => _transport.Port;

    public int ConnectionCount => _connections.Count;

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
        int connections = 0;
        int joined = 0;
        lock (_connectionTotalsSync)
        {
            bytesSent = Interlocked.Read(ref _closedBytesSent);
            framesSent = Interlocked.Read(ref _closedFramesSent);
            bytesReceived = Interlocked.Read(ref _closedBytesReceived);
            framesReceived = Interlocked.Read(ref _closedFramesReceived);
            coalesced = Interlocked.Read(ref _closedCoalescedSnapshots);
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
            _persistenceWrites?.FailedWrites ?? 0);
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
        _connections[connection.ConnectionId] = connection;
        Volatile.Write(ref _connectionsChanged, 1);
    }

    public async ValueTask<ServerConnection> AcceptNextClientAsync(CancellationToken cancellationToken = default)
    {
        ITransportConnection accepted = await _transport.AcceptClientAsync(cancellationToken);
        ServerConnection connection = new(accepted, _host, _codec, _framer);
        AttachConnection(connection);
        NetworkDiagnosticsLog.Write(
            "server.accept",
            $"connection={connection.ConnectionId} connections={_connections.Count}");
        _ = RunConnectionLoopAsync(connection, cancellationToken);
        return connection;
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

        long snapshotStart = ServerTickMetrics.Timestamp();
        ServerSnapshotMessage snapshot = _host.CreateSnapshotMessage();
        long broadcastStart = ServerTickMetrics.Timestamp();
        // Enqueue only: each connection's writer task does the socket I/O, so a stalled client
        // cannot hold up the tick loop for everyone else. The snapshot payload is the same for
        // every joined connection (it carries every session's accepted command sequence), so it
        // is serialized once, by the first writer that needs it, and each connection adds only
        // its own envelope.
        SharedEncodedPayload sharedPayload = new(_codec, snapshot);
        for (int i = 0; i < joinedConnections.Count; i++)
        {
            joinedConnections[i].EnqueueSnapshot(snapshot, sharedPayload);
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
    }

    public async ValueTask DisposeAsync()
    {
        foreach (ServerConnection connection in _connections.Values)
        {
            await connection.DisposeAsync();
        }

        _connections.Clear();
        Volatile.Write(ref _connectionsChanged, 1);
        await _transport.DisposeAsync();
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

    private async ValueTask DropFaultedConnectionsAsync()
    {
        RefreshConnectionList();
        for (int i = 0; i < _connectionList.Count; i++)
        {
            ServerConnection connection = _connectionList[i];
            if (!connection.IsFaulted || !TryRemoveConnection(connection))
            {
                continue;
            }

            _host.ReleaseConnection(connection.ConnectionId);
            NetworkDiagnosticsLog.Write(
                "server.disconnect",
                $"connection={connection.ConnectionId} reason=faulted connections={_connections.Count}");
            await connection.DisposeAsync();
        }
    }

    // Started fire-and-forget by AcceptNextClientAsync, so it must never end faulted: every error
    // is logged once and closes only this connection.
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
        catch (Exception ex)
        {
            ReportConnectionError(connection, "error", $"{ex.GetType().Name}: {ex.Message}", ex);
        }
        finally
        {
            try
            {
                if (TryRemoveConnection(connection))
                {
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
        }
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
