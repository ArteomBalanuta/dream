using System.Net;

public static class ServerBootstrap
{
    public const int DefaultPort = 5057;

    public static DedicatedServerHost CreateDefaultHost()
    {
        return DedicatedServerHost.CreateDefault();
    }

    public static DedicatedServerHost CreateAuthoritativeHost()
    {
        return DedicatedServerHost.CreateRuntimeAttached();
    }

    public static int RunServerMode()
    {
        return RunServerModeAsync().GetAwaiter().GetResult();
    }

    private static async Task<int> RunServerModeAsync()
    {
        using DedicatedServerHost host = CreateAuthoritativeHost();
        await using IServerTransport transport = new TcpServerTransport(IPAddress.Any, DefaultPort);
        await using DedicatedServerService service = new(
            host,
            transport,
            new TransportJsonCodec(),
            new LengthPrefixedMessageFramer());

        using CancellationTokenSource cancellation = new();
        ConsoleCancelEventHandler? cancelHandler = (_, args) =>
        {
            args.Cancel = true;
            cancellation.Cancel();
        };

        Console.CancelKeyPress += cancelHandler;
        try
        {
            Console.Error.WriteLine(
                $"Server listening on 0.0.0.0:{DefaultPort} with protocol version {TransportEnvelope.CurrentProtocolVersion}. " +
                "Single-session authoritative runtime is active; press Ctrl+C to stop.");
            await service.RunAsync(cancellation.Token);
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }
}
