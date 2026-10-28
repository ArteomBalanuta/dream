using System.Globalization;
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

    // Overrides the persisted world's simulation seed for one run (to reproduce a world's rolls)
    // without writing it back. Decimal or 0x-prefixed hexadecimal unsigned 64-bit integer.
    public const string SimulationSeedEnvironmentVariable = "DREAM_SIMULATION_SEED";

    public static DedicatedServerHost CreateDefaultHost()
    {
        return DedicatedServerHost.CreateDefault();
    }

    public static DedicatedServerHost CreateAuthoritativeHost()
    {
        return CreateAuthoritativeHost(CreatePersistenceBundle());
    }

    // The host the dedicated and integrated servers run. Joins are applied at the next tick in
    // canonical order, so same-tick joiners get the same seats, actor ids and spawns whatever
    // order their requests arrived in; DedicatedServerService delivers the replies.
    public static DedicatedServerHost CreateAuthoritativeHost(ServerPersistenceBundle? persistence)
    {
        return DedicatedServerHost.CreateRuntimeAttached(persistence, deferJoinsToTick: true);
    }

    public static int RunServerMode()
    {
        return RunServerModeAsync().GetAwaiter().GetResult();
    }

    private static async Task<int> RunServerModeAsync()
    {
        using DedicatedServerHost host = CreateAuthoritativeHost();
        await using IServerTransport transport = new TcpServerTransport(IPAddress.Any, DefaultPort);
        Action<string> log = static line => Console.Error.WriteLine(line);
        await using DedicatedServerService service = new(
            host,
            transport,
            new TransportJsonCodec(),
            FrameLimitOptions.FromEnvironment(log).CreateServerFramer(),
            ServerConnectionOptions.Default.WithEnvironmentOverrides(log));

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
        return CreatePersistenceBundle(
            AssetPaths.Config("player-profiles.db"),
            AssetPaths.Config("player-profiles.init.sql"),
            Environment.GetEnvironmentVariable(SimulationSeedEnvironmentVariable),
            static line => Console.Error.WriteLine(line));
    }

    // Opens (or creates) the persistent world and resolves the seed its runtime rolls with: the
    // seed stored in world_metadata, generated on first load, unless simulationSeedOverride holds
    // a valid seed. The override is applied for this run only and never stored.
    public static ServerPersistenceBundle CreatePersistenceBundle(
        string databasePath,
        string schemaScriptPath,
        string? simulationSeedOverride = null,
        Action<string>? log = null)
    {
        SqliteSchemaBootstrapper schema = new(databasePath, schemaScriptPath);
        schema.EnsureSchema();
        WorldSimulationSeed stored = new SqliteWorldMetadataRepository(schema).GetOrCreateSimulationSeed();
        if (stored.Created)
        {
            log?.Invoke("World simulation seed generated and stored in world_metadata.");
        }

        ulong simulationSeed = ResolveSimulationSeed(stored.Seed, simulationSeedOverride, log);
        SqliteCombatFormulaRepository formulas = new(schema);
        return new ServerPersistenceBundle(
            schema,
            new SqlitePlayerProfileRepository(schema),
            new SqliteWorldPersistenceRepository(schema),
            formulas,
            CombatFormulaService.Create(formulas),
            simulationSeed);
    }

    // The override wins when it parses; an unset value keeps the stored seed silently, and an
    // invalid one keeps it with a log line. The seed value itself is never logged.
    public static ulong ResolveSimulationSeed(ulong storedSeed, string? overrideValue, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(overrideValue))
        {
            return storedSeed;
        }

        if (TryParseSimulationSeed(overrideValue, out ulong overrideSeed))
        {
            log?.Invoke($"{SimulationSeedEnvironmentVariable} is set; this run uses it instead of the stored world seed.");
            return overrideSeed;
        }

        log?.Invoke(
            $"{SimulationSeedEnvironmentVariable} is not an unsigned 64-bit integer (decimal or 0x hex); " +
            "ignoring it and using the stored world seed.");
        return storedSeed;
    }

    public static bool TryParseSimulationSeed(string? value, out ulong seed)
    {
        seed = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string trimmed = value.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed.Length > 2 && ulong.TryParse(
                trimmed.AsSpan(2),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out seed);
        }

        return ulong.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out seed);
    }
}
