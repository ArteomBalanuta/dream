using System.Globalization;
using System.Net;
using Dream.Assets;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Server.Hosting;
using Dream.Server.Persistence;
using Dream.Server.Persistence.Migrations;
using Dream.Server.Persistence.Repositories;
using Microsoft.Data.Sqlite;
using Dream.Server.Persistence.Worlds;
using Dream.Server.Worlds;
using Dream.Simulation.Content;
using Dream.Simulation.Inventory;
using Dream.Simulation.Persistence;
using Dream.Simulation.Rules;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Simulation.World.Terrain;

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

    // Moves the world clock forward at start to the next tick showing this time of day
    // (see WorldTimeOfDay.TryParse for the accepted formats). Unset changes nothing.
    public const string TimeOfDayEnvironmentVariable = "DREAM_TIME_OF_DAY";

    // "1" lets clients use noclip, freeze and the stat sliders on the dedicated server. Off by default;
    // the integrated server turns it on itself.
    public const string DevCommandsEnvironmentVariable = DevCommandsOptions.EnvironmentVariable;

    // "1" lets players damage each other (M4). Off by default: with it off no player is ever a target.
    // One server process hosts one world, so this is per world in practice.
    public const string PvpEnvironmentVariable = "DREAM_PVP";

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
        RuntimePersistencePorts? ports = CreateRuntimePersistencePorts(persistence);
        RuntimeAuthoritativeSessionHost? host = null;
        try
        {
            host = RuntimeAuthoritativeSessionHost.CreateDefault(ports);
            host.DevCommandsAllowed = string.Equals(
                Environment.GetEnvironmentVariable(DevCommandsEnvironmentVariable), "1", StringComparison.Ordinal);
            host.Runtime.PvpEnabled = string.Equals(
                Environment.GetEnvironmentVariable(PvpEnvironmentVariable), "1", StringComparison.Ordinal);
            double dayLengthMinutes = ResolveDayLengthMinutes(
                Environment.GetEnvironmentVariable(DayLengthEnvironmentVariable),
                log);
            WorldClockSettings clockSettings = WorldClockSettings.FromDayLengthMinutes(dayLengthMinutes);
            long worldTicks = ResolveStartWorldTicks(
                clockSettings,
                persistence?.WorldTimeTicks ?? 0,
                Environment.GetEnvironmentVariable(TimeOfDayEnvironmentVariable),
                log);
            host.Runtime.ConfigureWorldClock(clockSettings, worldTicks);
            // The content's vendors (M3). Only the server host spawns them: a runtime built any other way
            // has none, so its snapshots and goldens do not change.
            host.Runtime.SpawnContentNpcs();
            return host;
        }
        catch
        {
            // A failed start (a corrupt stored terrain, say) must release the database: the host owns
            // the bundle once it exists; before that the write queue and the bundle are released here.
            if (host != null)
            {
                host.Dispose();
            }
            else
            {
                ports?.PersistenceWrites?.Dispose();
                persistence?.Dispose();
            }

            throw;
        }
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
            TerrainPayload = persistence.TerrainPayload,
            Accounts = persistence.Accounts,
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

    // The dedicated server's exit code when the database fails its start-up integrity check: a
    // foreign-key violation, or a damaged file. Only these stop a start; soft findings are logged.
    public const int DatabaseIntegrityExitCode = 6;

    // The dedicated server's exit code when another process already hosts the selected world.
    public const int WorldHeldExitCode = 7;

    // The dedicated server's exit code when a content file (config/content) is missing or invalid.
    public const int ContentInvalidExitCode = 8;

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
        catch (ContentValidationException ex)
        {
            log($"Server not started: the game content is invalid: {ex.Message}");
            exitCode = ContentInvalidExitCode;
            return null;
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
        catch (WorldHeldException ex)
        {
            log(
                $"Server not started: world {ex.WorldId} is already hosted by another process " +
                $"(lock file {ex.LockPath}). Stop that server. Selecting another world (--world, or DREAM_WORLD) only helps when no database migration is pending: a pending migration needs every world idle.");
            exitCode = WorldHeldExitCode;
            return null;
        }
        catch (LockFileUnavailableException ex)
        {
            log(
                $"Server not started: cannot create the lock file {ex.LockPath}: {ex.InnerException?.Message ?? ex.Message}. " +
                "Check that the database directory exists and is writable.");
            exitCode = WorldCannotBeOpenedExitCode;
            return null;
        }
        catch (DatabaseBusyException ex)
        {
            log(
                $"Server not started: the database is being restored or migrated by another process " +
                $"(lock file {ex.LockPath}). Try again when it has finished.");
            exitCode = WorldHeldExitCode;
            return null;
        }
        catch (DatabaseIntegrityException ex)
        {
            log($"Server not started: {ex.Message}. Restore a backup (--db restore <path> --yes) or repair the database.");
            exitCode = DatabaseIntegrityExitCode;
            return null;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26)
        {
            // SQLITE_CORRUPT or SQLITE_NOTADB from anywhere in the start (the migrator, say), before
            // the integrity check had a chance to run.
            log($"Server not started: the database file is damaged (SQLite error {ex.SqliteErrorCode}: {ex.Message}). " +
                "Restore a backup (--db restore <path> --yes).");
            exitCode = DatabaseIntegrityExitCode;
            return null;
        }
        catch (ArgumentException ex)
        {
            string label = string.IsNullOrWhiteSpace(worldSelector) ? "(default)" : worldSelector.Trim();
            log($"Server not started: world '{label}' cannot be opened: {ex.Message}");
            exitCode = WorldCannotBeOpenedExitCode;
            return null;
        }
        catch (StoredTerrainInvalidException ex)
        {
            // Bootstrap validates the stored terrain's rows and limits but does not decode the blobs;
            // a corrupt blob body surfaces here, from the runtime's map build.
            string label = string.IsNullOrWhiteSpace(worldSelector) ? "(default)" : worldSelector.Trim();
            log($"Server not started: world '{label}' cannot be opened: the stored terrain is invalid: {ex.Message}");
            exitCode = WorldCannotBeOpenedExitCode;
            return null;
        }
        catch (InvalidDataException ex)
        {
            // Any other invalid data at startup: no claim about the terrain.
            string label = string.IsNullOrWhiteSpace(worldSelector) ? "(default)" : worldSelector.Trim();
            log($"Server not started: world '{label}' cannot be opened: invalid data: {ex.Message}");
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
    // process environment. integrityOptions overrides the load-time check's options for this start;
    // it defaults to the shipped content's known kinds.
    public static ServerPersistenceBundle CreatePersistenceBundle(
        string databasePath,
        string schemaScriptPath,
        string? simulationSeedOverride = null,
        Action<string>? log = null,
        ServerStartOptions? start = null,
        IWorldCatalog? catalog = null,
        string? worldEnvironmentValue = null,
        WorldIntegrityOptions? integrityOptions = null)
    {
        // Every dedicated and integrated start comes through here, so a bad content file stops the
        // start before the database is touched, and JSON is never first parsed on a tick.
        ContentCatalog content = ReadStartupContent(static () => ContentCatalog.Shipped, log);

        SqliteSchemaBootstrapper schema = new(databasePath, schemaScriptPath);

        WorldSelector selector = ResolveWorldSelector(
            start?.WorldSelector,
            worldEnvironmentValue);
        int newWorldSize = start?.NewWorldSize ?? WorldDefaults.StartupFloorSize;
        IWorldCatalog worlds = catalog ?? new SqliteWorldCatalog(schema);

        // The database's maintenance lock covers the migration, the world resolve and the lease
        // acquisition, so a --db restore cannot swap the file between them and two starts never
        // migrate at once. A migration that is pending also needs every world's lease (a server
        // holding a world is never migrated under). A database that is not a file takes neither.
        WorldRecord record;
        bool created;
        WorldHostLease? lease;
        using (DatabaseMaintenanceLock.EnsureSchemaGuarded(schema, databasePath, log, start?.MaintenanceWait))
        {
            record = worlds.Resolve(selector, newWorldSize, out created);

            WorldDescriptor resolved = record.Descriptor;
            resolved.Validate();

            // One server per world on a database file. A database that is not a file takes no lease.
            // The lease belongs to the bundle from here on; a failure before the bundle exists
            // releases it.
            lease = WorldHostLease.AppliesTo(databasePath)
                ? WorldHostLease.Acquire(databasePath, resolved.Id)
                : null;
            if (lease != null)
            {
                // The record read before the lease can be stale (another process held the world, then
                // saved its world time and seed): read what the previous holder left.
                try
                {
                    // A null here is a world deleted between the resolve and the lease.
                    record = worlds.TryGet(resolved.Id) ?? throw new WorldNotFoundException(resolved.Id.ToString());
                }
                catch
                {
                    lease.Dispose();
                    throw;
                }
            }
        }

        try
        {
            record.Descriptor.Validate();
            return OpenWorldBundle(
                schema, record, created, simulationSeedOverride, log, lease, integrityOptions ?? IntegrityOptionsFor(content), content);
        }
        catch
        {
            lease?.Dispose();
            throw;
        }
    }

    // Reads the content a start runs with and logs its digest, which tells apart servers whose
    // content differs. A ContentValidationException from read stops the start (ContentInvalidExitCode).
    internal static ContentCatalog ReadStartupContent(Func<ContentCatalog> read, Action<string>? log)
    {
        ContentCatalog content = read();
        log?.Invoke(
            $"server.content.digest digest={content.ContentDigest} " +
            $"items={content.Items.Count} enemies={content.Enemies.Count} camps={content.Camps.Count}");
        if (content.Camps.Count == 0)
        {
            log?.Invoke("server.content.no-camps warning=spawns.json is missing or has no camps; the world keeps its single day wolf");
        }

        return content;
    }

    // Evaluates the formulas content depends on, so a bad formula (a division by zero, a non-finite
    // result) stops the start with ContentInvalidExitCode instead of throwing on a spawn or hit tick:
    // the enemy.* formulas for every template, and player.damage_taken_factor for every Armor 1..1000
    // (the effective range).
    internal static void ProbeFormulas(CombatFormulaService formulas, ContentCatalog content)
    {
        ArgumentNullException.ThrowIfNull(formulas);
        ArgumentNullException.ThrowIfNull(content);
        foreach (EnemyTemplate template in content.Enemies)
        {
            try
            {
                formulas.EvaluateEnemyDerivedStats(template.Strength, template.AttackSpeed, template.Vitality, template.Level, intervalOverride: 1f);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new ContentValidationException(
                    $"formula '{CombatFormulaKeys.EnemyAttackDamage}' or '{CombatFormulaKeys.EnemyMaxHealth}' failed for enemy '{template.Key}': {ex.Message}", ex);
            }

            try
            {
                formulas.EvaluateEnemyDerivedStats(template.Strength, template.AttackSpeed, template.Vitality, template.Level, intervalOverride: null);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new ContentValidationException(
                    $"formula '{CombatFormulaKeys.EnemyAttackIntervalSeconds}' failed for enemy '{template.Key}': {ex.Message}", ex);
            }
        }

        for (int armor = 1; armor <= 1000; armor++)
        {
            try
            {
                formulas.EvaluateDamageTakenFactor(armor);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new ContentValidationException(
                    $"formula '{CombatFormulaKeys.PlayerDamageTakenFactor}' failed at armor {armor}: {ex.Message}", ex);
            }
        }
    }

    // The integrity check's options for a build that knows content's kinds.
    public static WorldIntegrityOptions IntegrityOptionsFor(ContentCatalog content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new WorldIntegrityOptions
        {
            IsKnownItemKind = kind => content.TryGetItem(kind, out _),
            IsKnownEnemyKind = kind => content.TryGetEnemy(kind, out _),
            // An unknown kind is counted as such already, so it is not also a slot mismatch.
            CanOccupy = (kind, slotIndex) =>
                !content.TryGetItem(kind, out ItemDefinition? item)
                || (EquipmentSlots.TryFromSlotIndex(slotIndex, out EquipmentSlot slot)
                    && (item.EquipSlotMask & EquipmentSlots.MaskOf(slot)) != 0),
            FootprintOf = kind => content.TryGetItem(kind, out ItemDefinition? item) ? item.Footprint : ItemFootprint.One
        };
    }

    private static ServerPersistenceBundle OpenWorldBundle(
        SqliteSchemaBootstrapper schema,
        WorldRecord record,
        bool created,
        string? simulationSeedOverride,
        Action<string>? log,
        WorldHostLease? lease,
        WorldIntegrityOptions integrityOptions,
        ContentCatalog content)
    {
        WorldDescriptor descriptor = record.Descriptor;
        WorldId worldId = descriptor.Id;

        WorldIntegrityReport integrity;
        using (SqliteConnection connection = schema.CreateOpenConnection())
        {
            integrity = WorldIntegrityCheck.Run(connection, descriptor, integrityOptions);
        }

        foreach (string line in integrity.ToLogLines(descriptor))
        {
            log?.Invoke(line);
        }

        if (integrity.IsFatal)
        {
            throw new DatabaseIntegrityException(integrity.FatalMessage);
        }

        IReadOnlyList<PersistedTerrainChunk>? storedTerrain = null;
        TerrainTransferPayload? terrainPayload = null;
        if (descriptor.MapSource == WorldMapSource.Stored)
        {
            try
            {
                using (SqliteWorldPersistenceRepository terrainRepository = new(schema, worldId))
                {
                    storedTerrain = terrainRepository.TerrainChunks.LoadTerrainChunks();
                }

                TerrainTransferPayload payload = TerrainTransferPayload.Create(descriptor, storedTerrain);
                terrainPayload = payload;
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
        CombatFormulaService formulaService = CombatFormulaService.Create(formulas);
        ProbeFormulas(formulaService, content);
        return new ServerPersistenceBundle(
            schema,
            new SqlitePlayerProfileRepository(schema),
            new SqliteWorldPersistenceRepository(schema, worldId),
            formulas,
            formulaService,
            simulationSeed,
            record.WorldTimeTicks,
            worldId,
            descriptor,
            storedTerrain: storedTerrain,
            terrainPayload: terrainPayload,
            hostLease: lease,
            accounts: new SqliteAccountRepository(schema));
    }

    // The world ticks to start the clock at: storedTicks moved forward to the next tick showing the
    // DREAM_TIME_OF_DAY value (never backwards). Unset or blank changes nothing; an invalid value is
    // ignored with one log line. The chosen time is persisted by the normal checkpoints.
    public static long ResolveStartWorldTicks(WorldClockSettings settings, long storedTicks, string? timeOfDay, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(timeOfDay))
        {
            return storedTicks;
        }

        if (!WorldTimeOfDay.TryParse(timeOfDay, out double hours))
        {
            log?.Invoke(
                $"{TimeOfDayEnvironmentVariable} '{timeOfDay.Trim()}' is not HH:MM, a decimal hour (0-24) or " +
                "dawn/morning/noon/dusk/evening/midnight/night; ignoring it.");
            return storedTicks;
        }

        long forward = WorldTimeOfDay.ForwardTicks(settings, storedTicks, hours);
        log?.Invoke($"World time set to {WorldTimeOfDay.Format(hours)} by {TimeOfDayEnvironmentVariable}");
        return storedTicks + forward;
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
