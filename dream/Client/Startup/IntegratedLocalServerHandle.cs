using System.Net;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Server;

namespace Dream.Client.Startup;


public sealed class IntegratedLocalServerHandle : ILocalServerHandle
{
    // The integrated server runs in the player's own process and listens on loopback only, so
    // its one expected client cannot be "lost" the way a remote peer can: a window drag, a long
    // GC or a debugger break pauses the whole process, server tick included, and the liveness
    // clocks would see that pause as a dead client. The idle and send-stall checks are off; the
    // client's process exit still closes the socket, and the outbound backlog cap still bounds
    // memory.
    public static ServerConnectionOptions DefaultConnectionOptions { get; } = new()
    {
        IdleTimeout = TimeSpan.Zero,
        SendStallTimeout = TimeSpan.Zero
    };

    private readonly DedicatedServerHost? _host;
    private readonly DedicatedServerService? _service;
    private readonly CancellationTokenSource? _shutdown;
    private readonly Task? _runTask;

    private IntegratedLocalServerHandle(
        DedicatedServerHost? host,
        DedicatedServerService? service,
        CancellationTokenSource? shutdown,
        Task? runTask,
        bool isReady,
        int boundPort,
        string? errorMessage)
    {
        _host = host;
        _service = service;
        _shutdown = shutdown;
        _runTask = runTask;
        IsReady = isReady;
        BoundPort = boundPort;
        ErrorMessage = errorMessage;
    }

    public bool IsReady { get; }

    public int BoundPort { get; }

    public string? ErrorMessage { get; }

    public DedicatedServerPerformanceSnapshot LatestPerformance =>
        _service?.LatestPerformance ?? DedicatedServerPerformanceSnapshot.Empty;

    public static ValueTask<IntegratedLocalServerHandle> StartAsync(
        int preferredPort = 0,
        Func<DedicatedServerHost>? hostFactory = null,
        ServerConnectionOptions? connectionOptions = null)
    {
        hostFactory ??= ServerBootstrap.CreateAuthoritativeHost;
        connectionOptions ??= DefaultConnectionOptions;
        DedicatedServerHost? host = null;
        try
        {
            host = hostFactory();
            TcpServerTransport transport = new(IPAddress.Loopback, preferredPort);
            DedicatedServerService service = new(
                host,
                transport,
                new TransportJsonCodec(),
                new LengthPrefixedMessageFramer(),
                connectionOptions);
            service.Start();

            CancellationTokenSource shutdown = new();
            Task runTask = Task.Run(() => service.RunAsync(shutdown.Token));
            return ValueTask.FromResult(new IntegratedLocalServerHandle(
                host,
                service,
                shutdown,
                runTask,
                isReady: true,
                boundPort: service.Port,
                errorMessage: null));
        }
        catch (Exception ex)
        {
            // A failed start (the port is taken, say) must still release the host: it owns the
            // runtime's native physics world and, with persistence, the open database.
            host?.Dispose();
            return ValueTask.FromResult(new IntegratedLocalServerHandle(
                host: null,
                service: null,
                shutdown: null,
                runTask: null,
                isReady: false,
                boundPort: 0,
                errorMessage: ex.Message));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_shutdown != null)
        {
            _shutdown.Cancel();
        }

        // RunAsync returns only after every connection loop has released its seat, and
        // DisposeAsync waits for any loop still running, so the host is disposed last.
        if (_runTask != null)
        {
            try
            {
                await _runTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_service != null)
        {
            await _service.DisposeAsync();
        }

        _shutdown?.Dispose();
        _host?.Dispose();
    }
}
