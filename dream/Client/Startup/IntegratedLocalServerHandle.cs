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

    // internal, not private, only so the teardown tests can hand it a run task that already
    // faulted or was cancelled; StartAsync is the one production caller.
    internal IntegratedLocalServerHandle(
        DedicatedServerHost? host,
        DedicatedServerService? service,
        CancellationTokenSource? shutdown,
        Task? runTask,
        bool isReady,
        int boundPort,
        string? errorMessage,
        LocalServerListenOptions listenOptions,
        ServerConnectionOptions connectionOptions)
    {
        ListenOptions = listenOptions;
        ConnectionOptions = connectionOptions;
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

    public LocalServerListenOptions ListenOptions { get; }

    // What StartAsync left on the host: true for Single Player, the DREAM_DEV_COMMANDS value for a host.
    public bool DevCommandsAllowed => _host?.DevCommandsAllowed ?? false;

    public ServerConnectionOptions ConnectionOptions { get; }

    public DedicatedServerPerformanceSnapshot LatestPerformance =>
        _service?.LatestPerformance ?? DedicatedServerPerformanceSnapshot.Empty;

    public static ValueTask<IntegratedLocalServerHandle> StartAsync(
        int preferredPort = 0,
        Func<DedicatedServerHost>? hostFactory = null,
        ServerConnectionOptions? connectionOptions = null,
        LocalServerListenOptions? listen = null)
    {
        hostFactory ??= ServerBootstrap.CreateAuthoritativeHost;
        Action<string> log = static line => Console.Error.WriteLine(line);
        // listen overrides preferredPort and the bind address.
        LocalServerListenOptions effectiveListen = listen ?? new LocalServerListenOptions(IPAddress.Loopback, preferredPort, false);
        bool hosted = effectiveListen.AllowRemotePlayers;
        // DREAM_SNAPSHOT_INTERVAL_TICKS applies to the integrated server as to the dedicated one;
        // options passed in are used as given. Hosting follows the dedicated server's connection rules.
        int maxPlayers = hosted
            ? ServerRuntimeConfig.Parse(static n => n == ServerRuntimeConfig.MaxPlayersEnvironmentVariable ? Environment.GetEnvironmentVariable(n) : null, log)?.MaxPlayers ?? ServerRuntimeConfig.DefaultMaxPlayers
            : ServerRuntimeConfig.DefaultMaxPlayers;
        connectionOptions ??= hosted
            ? ServerConnectionOptions.Default.WithEnvironmentOverrides(log).WithPlayerCapHeadroom(
                maxPlayers,
                Environment.GetEnvironmentVariable(ServerConnectionOptions.MaxConnectionsEnvironmentVariable),
                log)
            : DefaultConnectionOptions.WithEnvironmentOverrides(log);
        DedicatedServerHost? host = null;
        try
        {
            host = hostFactory();
            if (hosted)
            {
                // Dev commands keep the environment-derived value ServerBootstrap set, as on the dedicated server.
                host.MaxPlayers = maxPlayers;
            }
            else
            {
                host.DevCommandsAllowed = true;
            }

            TcpServerTransport transport = new(effectiveListen.BindAddress, effectiveListen.Port);
            DedicatedServerService service = new(
                host,
                transport,
                new TransportJsonCodec(),
                // Directional limits (FrameLimitOptions): it reads small client frames and writes
                // snapshots and worlds up to the server-to-client cap.
                FrameLimitOptions.FromEnvironment(static line => Console.Error.WriteLine(line)).CreateServerFramer(),
                connectionOptions);
            service.Start();
            if (hosted)
            {
                Console.Error.WriteLine($"[host] hosting on {effectiveListen.BindAddress}:{service.Port}");
            }

            CancellationTokenSource shutdown = new();
            Task runTask = Task.Run(() => service.RunAsync(shutdown.Token));
            return ValueTask.FromResult(new IntegratedLocalServerHandle(
                host,
                service,
                shutdown,
                runTask,
                isReady: true,
                boundPort: service.Port,
                errorMessage: null,
                listenOptions: effectiveListen,
                connectionOptions: connectionOptions));
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
                : ex is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AddressAlreadyInUse }
                    ? $"Port {effectiveListen.Port} is already in use on this computer. Choose another port."
                    : ex is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AccessDenied }
                        ? $"Port {effectiveListen.Port} is reserved or not permitted on this computer. Choose another port."
                        : ex.Message;
            Console.Error.WriteLine($"[startup] local server not started: {message}");
            return ValueTask.FromResult(new IntegratedLocalServerHandle(
                host: null,
                service: null,
                shutdown: null,
                runTask: null,
                isReady: false,
                boundPort: 0,
                errorMessage: message,
                listenOptions: effectiveListen,
                connectionOptions: connectionOptions));
        }
    }

    // How long the stop waits for the server's run task before it tears the server down anyway.
    // RunAsync normally returns within a tick of the cancellation, and its own finally already
    // bounds the connection loops (DedicatedServerService.ConnectionLoopShutdownTimeout, 5 s), so
    // this is the backstop for a run task that is wedged rather than slow: the teardown below --
    // the queued writes, the final checkpoint and the world lease -- matters more than waiting for
    // it, and the host's own disposal waits for an in-flight tick under the tick lock regardless.
    public static readonly TimeSpan RunTaskShutdownTimeout = TimeSpan.FromSeconds(10);

    public async ValueTask DisposeAsync()
    {
        if (_shutdown != null)
        {
            _shutdown.Cancel();
        }

        // The run task is awaited inside a try whose finally owns the teardown, because an
        // exception out of any tick (DedicatedServerHost.AdvanceTime and AdvanceOnceAsync have no
        // catch, and RunAsync's only catch is OperationCanceledException) faults this task -- and
        // before this finally existed, that fault rethrew here and skipped every statement after
        // it. The integrated server would then abandon its queued writes, its final checkpoint, its
        // world lease (so every later Single Player or Host start in this process failed with
        // "World N is already in use") and its listening socket, while the client carried on to the
        // title screen. The teardown now runs on all three outcomes -- completed, cancelled and
        // faulted -- and exactly once, since each step below is itself idempotent.
        try
        {
            // RunAsync returns only after every connection loop has released its seat, and
            // DisposeAsync waits for any loop still running, so the host is disposed last.
            if (_runTask != null)
            {
                await _runTask.WaitAsync(RunTaskShutdownTimeout);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Logged here rather than rethrown, and logged before the teardown runs so the trace
            // survives even if a step of the teardown throws in its turn. The stop itself is what
            // matters now, and PlaySessionEndTransition's callers only ever logged this fault
            // anyway (StopLocalServer / StopLocalServerForExit hand it back as a non-fatal line).
            Console.Error.WriteLine(
                $"[server] the local server's run loop failed ({ex.GetType().Name}: {ex.Message}); " +
                "stopping it and saving the world anyway");
        }
        finally
        {
            try
            {
                if (_service != null)
                {
                    await _service.DisposeAsync();
                }
            }
            finally
            {
                _shutdown?.Dispose();
                // Last and unconditionally: the host is the only thing that drains the persistence
                // write queue, writes the final checkpoint and releases the world lease, so a
                // throw out of the service's disposal must not cost us those.
                _host?.Dispose();
            }
        }
    }
}
