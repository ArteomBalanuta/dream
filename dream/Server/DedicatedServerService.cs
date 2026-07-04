using System.Collections.Concurrent;
using System.Diagnostics;
using Dream.Multiplayer.Diagnostics;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Simulation.World;

namespace Dream.Server;


public sealed class DedicatedServerService : IAsyncDisposable
{
    private readonly DedicatedServerHost _host;
    private readonly IServerTransport _transport;
    private readonly IProtocolCodec _codec;
    private readonly IMessageFramer _framer;
    private readonly ConcurrentDictionary<string, ServerConnection> _connections = new(StringComparer.Ordinal);
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

    public async ValueTask<int> AdvanceOnceAsync(double elapsedSeconds, CancellationToken cancellationToken = default)
    {
        Stopwatch? advanceStopwatch = NetworkDiagnosticsLog.IsEnabled ? Stopwatch.StartNew() : null;
        int executedTicks = _host.AdvanceTime(elapsedSeconds);
        advanceStopwatch?.Stop();
        if (executedTicks <= 0 || _connections.IsEmpty)
        {
            return executedTicks;
        }

        ServerConnection[] joinedConnections = _connections.Values
            .Where(connection => _host.IsConnectionJoined(connection.ConnectionId))
            .ToArray();
        if (joinedConnections.Length == 0)
        {
            NetworkDiagnosticsLog.WriteEvery(
                "server.tick.unjoined",
                TimeSpan.FromSeconds(1),
                "server.tick",
                () => $"ticks={executedTicks} connections={_connections.Count} joined=0 advanceMs={advanceStopwatch?.Elapsed.TotalMilliseconds ?? 0d:0.00}");
            return executedTicks;
        }

        Stopwatch? snapshotStopwatch = NetworkDiagnosticsLog.IsEnabled ? Stopwatch.StartNew() : null;
        ServerSnapshotMessage snapshot = _host.CreateSnapshotMessage();
        snapshotStopwatch?.Stop();
        Stopwatch? broadcastStopwatch = NetworkDiagnosticsLog.IsEnabled ? Stopwatch.StartNew() : null;
        foreach (ServerConnection connection in joinedConnections)
        {
            await connection.SendSnapshotAsync(snapshot, cancellationToken);
        }
        broadcastStopwatch?.Stop();

        IReadOnlyList<AuthoritativeActionResult> pendingActionResults = _host.DrainPendingActionResults();
        if (pendingActionResults.Count > 0)
        {
            foreach (AuthoritativeActionResult actionResult in pendingActionResults)
            {
                ServerConnection[] targetConnections = joinedConnections
                    .Where(connection =>
                        _host.TryGetConnectionContext(connection.ConnectionId, out ServerRoutingContext? context)
                        && context?.LastJoinRequest?.RequestedSessionId == actionResult.SessionId)
                    .ToArray();

                foreach (ServerConnection connection in targetConnections)
                {
                    await connection.SendActionAsync(new ServerActionMessage(actionResult), cancellationToken);
                }
            }
        }

        NetworkDiagnosticsLog.WriteEvery(
            "server.tick.joined",
            TimeSpan.FromSeconds(1),
            "server.tick",
            () => $"ticks={executedTicks} elapsed={elapsedSeconds * 1000d:0.00}ms connections={_connections.Count} " +
                $"joined={joinedConnections.Length} advanceMs={advanceStopwatch?.Elapsed.TotalMilliseconds ?? 0d:0.00} " +
                $"snapshotMs={snapshotStopwatch?.Elapsed.TotalMilliseconds ?? 0d:0.00} " +
                $"broadcastMs={broadcastStopwatch?.Elapsed.TotalMilliseconds ?? 0d:0.00} actions={pendingActionResults.Count} " +
                NetworkDiagnosticsLog.DescribeSnapshot(snapshot.Snapshot));

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
        await _transport.DisposeAsync();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await AcceptNextClientAsync(cancellationToken);
        }
    }

    private async Task TickLoopAsync(CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!cancellationToken.IsCancellationRequested)
        {
            double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
            stopwatch.Restart();
            await AdvanceOnceAsync(elapsedSeconds, cancellationToken);
            await Task.Delay(1, cancellationToken);
        }
    }

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
        finally
        {
            _connections.TryRemove(connection.ConnectionId, out _);
            _host.ReleaseConnection(connection.ConnectionId);
            NetworkDiagnosticsLog.Write(
                "server.disconnect",
                $"connection={connection.ConnectionId} connections={_connections.Count}");
            await connection.DisposeAsync();
        }
    }
}
