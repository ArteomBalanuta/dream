using System.Collections.Concurrent;
using System.Diagnostics;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Simulation.World;

namespace Dream.Server;


public sealed class DedicatedServerService : IAsyncDisposable
{
    private static readonly DedicatedServerPerformanceSnapshot UntimedPerformance = new(0d, 0d, 0d, Sampled: true);

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
    private int _connectionsChanged = 1;
    private bool _started;

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
    }

    public int Port => _transport.Port;

    public int ConnectionCount => _connections.Count;

    public DedicatedServerPerformanceSnapshot LatestPerformance { get; private set; } =
        DedicatedServerPerformanceSnapshot.Empty;

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
        Stopwatch? advanceStopwatch = NetworkDiagnosticsLog.IsEnabled ? Stopwatch.StartNew() : null;
        int executedTicks = _host.AdvanceTime(elapsedSeconds);
        advanceStopwatch?.Stop();
        if (executedTicks <= 0 || _connections.IsEmpty)
        {
            if (executedTicks > 0)
            {
                _host.DiscardUndeliveredSnapshotDeltas();
            }

            LatestPerformance = CreatePerformance(advanceStopwatch, snapshotStopwatch: null, broadcastStopwatch: null);
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
            LatestPerformance = CreatePerformance(advanceStopwatch, snapshotStopwatch: null, broadcastStopwatch: null);
            if (NetworkDiagnosticsLog.IsEnabled)
            {
                LogUnjoinedTick(executedTicks, advanceStopwatch);
            }

            return executedTicks;
        }

        Stopwatch? snapshotStopwatch = NetworkDiagnosticsLog.IsEnabled ? Stopwatch.StartNew() : null;
        ServerSnapshotMessage snapshot = _host.CreateSnapshotMessage();
        snapshotStopwatch?.Stop();
        Stopwatch? broadcastStopwatch = NetworkDiagnosticsLog.IsEnabled ? Stopwatch.StartNew() : null;
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
        broadcastStopwatch?.Stop();

        LatestPerformance = CreatePerformance(advanceStopwatch, snapshotStopwatch, broadcastStopwatch);

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
                advanceStopwatch,
                snapshotStopwatch,
                broadcastStopwatch,
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

    private async ValueTask DropFaultedConnectionsAsync()
    {
        RefreshConnectionList();
        for (int i = 0; i < _connectionList.Count; i++)
        {
            ServerConnection connection = _connectionList[i];
            if (!connection.IsFaulted || !_connections.TryRemove(connection.ConnectionId, out _))
            {
                continue;
            }

            Volatile.Write(ref _connectionsChanged, 1);
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
                if (_connections.TryRemove(connection.ConnectionId, out _))
                {
                    Volatile.Write(ref _connectionsChanged, 1);
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

    private static DedicatedServerPerformanceSnapshot CreatePerformance(
        Stopwatch? advanceStopwatch,
        Stopwatch? snapshotStopwatch,
        Stopwatch? broadcastStopwatch)
    {
        if (advanceStopwatch == null && snapshotStopwatch == null && broadcastStopwatch == null)
        {
            return UntimedPerformance;
        }

        return new DedicatedServerPerformanceSnapshot(
            advanceStopwatch?.Elapsed.TotalMilliseconds ?? 0d,
            snapshotStopwatch?.Elapsed.TotalMilliseconds ?? 0d,
            broadcastStopwatch?.Elapsed.TotalMilliseconds ?? 0d,
            Sampled: true);
    }

    private void LogUnjoinedTick(int executedTicks, Stopwatch? advanceStopwatch)
    {
        NetworkDiagnosticsLog.WriteEvery(
            "server.tick.unjoined",
            TimeSpan.FromSeconds(1),
            "server.tick",
            () => $"ticks={executedTicks} connections={_connections.Count} joined=0 advanceMs={advanceStopwatch?.Elapsed.TotalMilliseconds ?? 0d:0.00}");
    }

    private void LogJoinedTick(
        int executedTicks,
        double elapsedSeconds,
        int joinedCount,
        int actionCount,
        Stopwatch? advanceStopwatch,
        Stopwatch? snapshotStopwatch,
        Stopwatch? broadcastStopwatch,
        ServerSnapshotMessage snapshot)
    {
        NetworkDiagnosticsLog.WriteEvery(
            "server.tick.joined",
            TimeSpan.FromSeconds(1),
            "server.tick",
            () => $"ticks={executedTicks} elapsed={elapsedSeconds * 1000d:0.00}ms connections={_connections.Count} " +
                $"joined={joinedCount} advanceMs={advanceStopwatch?.Elapsed.TotalMilliseconds ?? 0d:0.00} " +
                $"snapshotMs={snapshotStopwatch?.Elapsed.TotalMilliseconds ?? 0d:0.00} " +
                $"broadcastMs={broadcastStopwatch?.Elapsed.TotalMilliseconds ?? 0d:0.00} actions={actionCount} " +
                NetworkDiagnosticsLog.DescribeSnapshot(snapshot.Snapshot));
    }
}

// Why the service closed a connection: kind is "protocol-mismatch", "error" or "cleanup-error";
// reason is the logged text.
public sealed record ServerConnectionError(string ConnectionId, string Kind, string Reason, Exception Exception);
