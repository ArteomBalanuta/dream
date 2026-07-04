using System.Net;
using Dream.Assets;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Server.Persistence;
using Dream.Server.Rules;

namespace Dream.Server;


public static class ServerBootstrap
{
    public const int DefaultPort = 5057;

    public static DedicatedServerHost CreateDefaultHost()
    {
        return DedicatedServerHost.CreateDefault();
    }

    public static DedicatedServerHost CreateAuthoritativeHost()
    {
        return DedicatedServerHost.CreateRuntimeAttached(CreatePersistenceBundle());
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

    private static ServerPersistenceBundle CreatePersistenceBundle()
    {
        SqliteSchemaBootstrapper schema = new(
            AssetPaths.Config("player-profiles.db"),
            AssetPaths.Config("player-profiles.init.sql"));
        schema.EnsureSchema();
        SqliteCombatFormulaRepository formulas = new(schema);
        return new ServerPersistenceBundle(
            schema,
            new SqlitePlayerProfileRepository(schema),
            new SqliteWorldPersistenceRepository(schema),
            formulas,
            CombatFormulaService.Create(formulas));
    }
}
