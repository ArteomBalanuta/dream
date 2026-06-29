using System.Net;

public sealed class IntegratedLocalServerHandle : ILocalServerHandle
{
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

    public static ValueTask<IntegratedLocalServerHandle> StartAsync(
        int preferredPort = 0,
        Func<DedicatedServerHost>? hostFactory = null)
    {
        hostFactory ??= ServerBootstrap.CreateAuthoritativeHost;
        try
        {
            DedicatedServerHost host = hostFactory();
            TcpServerTransport transport = new(IPAddress.Loopback, preferredPort);
            DedicatedServerService service = new(
                host,
                transport,
                new TransportJsonCodec(),
                new LengthPrefixedMessageFramer());
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
