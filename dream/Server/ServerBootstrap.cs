using System.Globalization;
using System.Net;
using Dream.Assets;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Server.Persistence;
using Dream.Server.Persistence.Migrations;
using Dream.Server.Persistence.Worlds;
using Dream.Server.Worlds;
using Dream.Simulation.Persistence;
using Dream.Simulation.Rules;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Server;


public static class ServerBootstrap
{
    public const int DefaultPort = 5057;

    // Overrides the persisted world's simulation seed for one run (to reproduce a world's rolls)
    // without writing it back. Decimal or 0x-prefixed hexadecimal unsigned 64-bit integer.
    public const string SimulationSeedEnvironmentVariable = "DREAM_SIMULATION_SEED";

    // Real-time minutes per in-game day (the world clock's TicksPerDay). Not persisted: changing it
    // between runs keeps WorldTicks, so the time of day jumps once on restart.
    public const string DayLengthEnvironmentVariable = "DREAM_DAY_LENGTH_MINUTES";
    public const double MinDayLengthMinutes = 1d;
    public const double MaxDayLengthMinutes = 1440d;

    // Selects the world a dedicated server opens when no --world argument is given: a world id or
    // a world name. Unset means the default world. (Read from persistence wave B2 on.)
    public const string WorldEnvironmentVariable = "DREAM_WORLD";

    // The dedicated server's database and its initial schema script, next to the executable.
    public static string DefaultDatabasePath => AssetPaths.Config("player-profiles.db");

    public static string DefaultSchemaScriptPath => AssetPaths.Config("player-profiles.init.sql");

    // The server's default log: each line to stderr.
    private static readonly Action<string> ConsoleLog = static line => Console.Error.WriteLine(line);

    public static DedicatedServerHost CreateDefaultHost()
    {
        return DedicatedServerHost.CreateDefault();
    }

    public static DedicatedServerHost CreateAuthoritativeHost()
    {
        return CreateAuthoritativeHost(CreatePersistenceBundle());
    }

    // The authoritative host over the default database, opening the world the options select.
    public static DedicatedServerHost CreateAuthoritativeHost(ServerStartOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return CreateAuthoritativeHost(CreatePersistenceBundle(options));
    }

    // The host the dedicated and integrated servers run. Joins are applied at the next tick in
    // canonical order, so same-tick joiners get the same seats, actor ids and spawns whatever
    // order their requests arrived in; DedicatedServerService delivers the replies.
    public static DedicatedServerHost CreateAuthoritativeHost(ServerPersistenceBundle? persistence)
    {
        return DedicatedServerHost.CreateRuntimeAttached(persistence, deferJoinsToTick: true);
    }

    // The authoritative runtime host over the server's SQLite persistence. The bundle's repositories
    // and formulas become the Simulation-owned RuntimePersistencePorts, with a SQLite checkpoint
    // writer and a background write queue; the host owns the bundle and disposes it. A null bundle
    // gives a host that persists nothing. The world clock resumes from the bundle's stored world
    // time (0 for a new world or no bundle) before the first tick.
    public static RuntimeAuthoritativeSessionHost CreateRuntimeHost(ServerPersistenceBundle? persistence)
    {
        return CreateRuntimeHost(persistence, ConsoleLog);
    }

    // As above; a DREAM_DAY_LENGTH_MINUTES warning goes to log (nowhere when it is null), like
    // CreatePersistenceBundle's seed lines.
    public static RuntimeAuthoritativeSessionHost CreateRuntimeHost(ServerPersistenceBundle? persistence, Action<string>? log)
    {
        RuntimeAuthoritativeSessionHost host = RuntimeAuthoritativeSessionHost.CreateDefault(CreateRuntimePersistencePorts(persistence));
        double dayLengthMinutes = ResolveDayLengthMinutes(
            Environment.GetEnvironmentVariable(DayLengthEnvironmentVariable),
            log);
        host.Runtime.ConfigureWorldClock(
            WorldClockSettings.FromDayLengthMinutes(dayLengthMinutes),
            persistence?.WorldTimeTicks ?? 0);
        return host;
    }

    public static RuntimePersistencePorts? CreateRuntimePersistencePorts(ServerPersistenceBundle? persistence)
    {
        if (persistence == null)
        {
            return null;
        }

        return new RuntimePersistencePorts(
            PlayerProfiles: persistence.PlayerProfiles,
            World: persistence.World,
            FormulaService: persistence.FormulaService,
            CheckpointWriter: new SqlitePersistenceCheckpointWriter(persistence.Schema, persistence.WorldId),
            PersistenceWrites: PersistenceWriteQueue.Background(),
            SimulationSeed: persistence.SimulationSeed,
            Owner: persistence)
        {
            Descriptor = persistence.Descriptor,
            StoredTerrain = persistence.StoredTerrain,
        };
    }

    public static int RunServerMode()
    {
        return RunServerMode(ServerStartOptions.Default);
    }

    public static int RunServerMode(ServerStartOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return RunServerModeAsync(options).GetAwaiter().GetResult();
    }

    // The dedicated server's exit code when the database was migrated by a newer build.
    public const int SchemaTooNewExitCode = 3;

    // The dedicated server's exit code when the selected world does not exist.
    public const int WorldNotFoundExitCode = 4;

    // The dedicated server's exit code when the selected world cannot be opened: an invalid name
    // (over 64 characters), or a stored world this build cannot host (size, height, generator).
    public const int WorldCannotBeOpenedExitCode = 5;

    // The host, or null after logging why the server cannot start: a database at a schema version
    // newer than this build (SchemaTooNewException) is a clear operator error, not a crash.
    internal static DedicatedServerHost? TryCreateServerModeHost(
        Func<DedicatedServerHost> createHost,
        Action<string> log,
        out int exitCode,
        string? worldSelector = null)
    {
        try
        {
            exitCode = 0;
            return createHost();
        }
        catch (SchemaTooNewException ex)
        {
            log(
                $"Server not started: the database schema is at version {ex.StoredVersion}, newer than the " +
                $"version {ex.LatestVersion} this build knows. Run a newer build, or restore a backup " +
                "made before the upgrade (the .bak next to the database).");
            exitCode = SchemaTooNewExitCode;
            return null;
        }
        catch (WorldNotFoundException ex)
        {
            log(
                $"Server not started: world '{ex.Selector}' does not exist. " +
                "Select an existing world id or name (--world, or DREAM_WORLD).");
            exitCode = WorldNotFoundExitCode;
            return null;
        }
        catch (ArgumentException ex)
        {
            string label = string.IsNullOrWhiteSpace(worldSelector) ? "(default)" : worldSelector.Trim();
            log($"Server not started: world '{label}' cannot be opened: {ex.Message}");
            exitCode = WorldCannotBeOpenedExitCode;
            return null;
        }
        catch (InvalidDataException ex)
        {
            // Bootstrap validates the stored terrain's rows and limits but does not decode the blobs;
            // a corrupt blob body surfaces here, from the runtime's map build.
            string label = string.IsNullOrWhiteSpace(worldSelector) ? "(default)" : worldSelector.Trim();
            log($"Server not started: world '{label}' cannot be opened: the stored terrain is invalid: {ex.Message}");
            exitCode = WorldCannotBeOpenedExitCode;
            return null;
        }
    }

    private static async Task<int> RunServerModeAsync(ServerStartOptions options)
    {
        using DedicatedServerHost? host = TryCreateServerModeHost(
            () => CreateAuthoritativeHost(options),
            ConsoleLog,
            out int startupExitCode,
            string.IsNullOrWhiteSpace(options.WorldSelector)
                ? Environment.GetEnvironmentVariable(WorldEnvironmentVariable)
                : options.WorldSelector);
        if (host == null)
        {
            return startupExitCode;
        }

        await using IServerTransport transport = new TcpServerTransport(IPAddress.Any, DefaultPort);
        Action<string> log = ConsoleLog;
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

    private static ServerPersistenceBundle CreatePersistenceBundle(ServerStartOptions? start = null)
    {
        return CreatePersistenceBundle(
            DefaultDatabasePath,
            DefaultSchemaScriptPath,
            Environment.GetEnvironmentVariable(SimulationSeedEnvironmentVariable),
            ConsoleLog,
            start,
            worldEnvironmentValue: Environment.GetEnvironmentVariable(WorldEnvironmentVariable));
    }

    // The world a start selects: the --world argument, else DREAM_WORLD, else the default world.
    // A selector that cannot be a world id or name (a digit text that is not a valid id) throws
    // WorldNotFoundException, like an id that does not exist.
    public static WorldSelector ResolveWorldSelector(string? argument, string? environmentValue)
    {
        string? text = string.IsNullOrWhiteSpace(argument) ? environmentValue : argument;
        try
        {
            return WorldSelector.Parse(text);
        }
        catch (ArgumentException)
        {
            throw new WorldNotFoundException(text!.Trim());
        }
    }

    // Opens (or creates) the persistent world the start selects (see ResolveWorldSelector) and
    // resolves the seed its runtime rolls with: the seed stored on the world, generated on first
    // load, unless simulationSeedOverride holds a valid seed. The override is applied for this run
    // only and never stored. The repositories are bound to the resolved world's id. The log names
    // the world (name and id) and never the seed. catalog is a test seam; it defaults to the
    // database's SqliteWorldCatalog. worldEnvironmentValue is the DREAM_WORLD value the caller read
    // (the entry points above read it); it defaults to none, so a direct call never depends on the
    // process environment.
    public static ServerPersistenceBundle CreatePersistenceBundle(
        string databasePath,
        string schemaScriptPath,
        string? simulationSeedOverride = null,
        Action<string>? log = null,
        ServerStartOptions? start = null,
        IWorldCatalog? catalog = null,
        string? worldEnvironmentValue = null)
    {
        SqliteSchemaBootstrapper schema = new(databasePath, schemaScriptPath);
        schema.EnsureSchema(log);

        WorldSelector selector = ResolveWorldSelector(
            start?.WorldSelector,
            worldEnvironmentValue);
        int newWorldSize = start?.NewWorldSize ?? WorldDefaults.StartupFloorSize;

        WorldRecord record = (catalog ?? new SqliteWorldCatalog(schema)).Resolve(selector, newWorldSize, out bool created);

        WorldDescriptor descriptor = record.Descriptor;
        descriptor.Validate();

        WorldId worldId = descriptor.Id;
        IReadOnlyList<PersistedTerrainChunk>? storedTerrain = null;
        if (descriptor.MapSource == WorldMapSource.Stored)
        {
            try
            {
                storedTerrain = new SqliteWorldPersistenceRepository(schema, worldId).TerrainChunks.LoadTerrainChunks();
                TerrainTransferPayload payload = TerrainTransferPayload.Create(descriptor, storedTerrain);
                log?.Invoke(
                    $"World '{descriptor.Name}' (id {worldId}) stored terrain: chunks={payload.Manifest.Chunks} " +
                    $"bytes={payload.Manifest.Bytes} sha256={payload.Manifest.Sha256}");
            }
            catch (InvalidDataException ex)
            {
                // Mapped to exit 5 by TryCreateServerModeHost (ArgumentException).
                throw new ArgumentException($"the stored terrain is invalid: {ex.Message}", nameof(record), ex);
            }
        }

        log?.Invoke(created
            ? $"World '{descriptor.Name}' (id {worldId}) did not exist and was created (size {descriptor.Width}). " +
              "If this was a typo, stop the server and remove it with: --worlds delete <id> --yes."
            : $"World '{descriptor.Name}' (id {worldId}) opened.");

        ulong storedSeed;
        if (record.Seed is ulong recordSeed)
        {
            storedSeed = recordSeed;
        }
        else
        {
            WorldSimulationSeed generated = new SqliteWorldMetadataRepository(schema, worldId).GetOrCreateSimulationSeed();
            storedSeed = generated.Seed;
            if (generated.Created)
            {
                log?.Invoke($"World '{descriptor.Name}' simulation seed generated and stored.");
            }
        }

        ulong simulationSeed = ResolveSimulationSeed(storedSeed, simulationSeedOverride, log);
        SqliteCombatFormulaRepository formulas = new(schema);
        return new ServerPersistenceBundle(
            schema,
            new SqlitePlayerProfileRepository(schema),
            new SqliteWorldPersistenceRepository(schema, worldId),
            formulas,
            CombatFormulaService.Create(formulas),
            simulationSeed,
            record.WorldTimeTicks,
            worldId,
            descriptor,
            storedTerrain: storedTerrain);
    }

    // Minutes per in-game day: an unset value gives the default silently; a number outside
    // [MinDayLengthMinutes, MaxDayLengthMinutes] is clamped and an unparsable one falls back to
    // the default, each with a log line.
    public static double ResolveDayLengthMinutes(string? value, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return WorldClockSettings.DefaultDayLengthMinutes;
        }

        if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double minutes) ||
            !double.IsFinite(minutes))
        {
            log?.Invoke(
                $"{DayLengthEnvironmentVariable} is not a number of minutes; " +
                $"using the default of {WorldClockSettings.DefaultDayLengthMinutes}.");
            return WorldClockSettings.DefaultDayLengthMinutes;
        }

        double clamped = Math.Clamp(minutes, MinDayLengthMinutes, MaxDayLengthMinutes);
        if (clamped != minutes)
        {
            log?.Invoke(
                $"{DayLengthEnvironmentVariable} is outside {MinDayLengthMinutes}-{MaxDayLengthMinutes} minutes; " +
                $"using {clamped.ToString(CultureInfo.InvariantCulture)}.");
        }

        return clamped;
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
