using System.Net;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Server.Hosting;

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

    // CONTRACT-STUB: WS-B sets these from the start.
    public LocalServerListenOptions ListenOptions { get; } = LocalServerListenOptions.Loopback;

    public ServerConnectionOptions ConnectionOptions { get; } = DefaultConnectionOptions;

    public DedicatedServerPerformanceSnapshot LatestPerformance =>
        _service?.LatestPerformance ?? DedicatedServerPerformanceSnapshot.Empty;

    public static ValueTask<IntegratedLocalServerHandle> StartAsync(
        int preferredPort = 0,
        Func<DedicatedServerHost>? hostFactory = null,
        ServerConnectionOptions? connectionOptions = null,
        LocalServerListenOptions? listen = null)
    {
        // CONTRACT-STUB: WS-B honours listen (bind address, port, hosted connection rules).
        hostFactory ??= ServerBootstrap.CreateAuthoritativeHost;
        // DREAM_SNAPSHOT_INTERVAL_TICKS applies to the integrated server as to the dedicated one;
        // options passed in are used as given.
        connectionOptions ??= DefaultConnectionOptions.WithEnvironmentOverrides(static line => Console.Error.WriteLine(line));
        DedicatedServerHost? host = null;
        try
        {
            host = hostFactory();
            host.DevCommandsAllowed = true;
            TcpServerTransport transport = new(IPAddress.Loopback, preferredPort);
            DedicatedServerService service = new(
                host,
                transport,
                new TransportJsonCodec(),
                // Directional limits (FrameLimitOptions): it reads small client frames and writes
                // snapshots and worlds up to the server-to-client cap.
                FrameLimitOptions.FromEnvironment(static line => Console.Error.WriteLine(line)).CreateServerFramer(),
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
            // The connection screen shows the message and Connect stays usable, so a client whose
            // local server stayed off can still join another server.
            string message = ex is WorldHeldException held
                ? $"World {held.WorldId} is already in use by another Dream window or server on this computer. " +
                  "Close it to host here, or enter a server address to join."
                : ex.Message;
            Console.Error.WriteLine($"[startup] local server not started: {message}");
            return ValueTask.FromResult(new IntegratedLocalServerHandle(
                host: null,
                service: null,
                shutdown: null,
                runTask: null,
                isReady: false,
                boundPort: 0,
                errorMessage: message));
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
