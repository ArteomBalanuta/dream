using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Persistence;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using OpenTK.Mathematics;

namespace Dream.Tests.Load;

// Runs one LoadScenario end to end: seeds a temp SQLite world, starts the production server
// (DedicatedServerService over loopback TCP on port 0, host from ServerBootstrap), connects the
// bots, drives them at the command rate for the scenario duration, samples server metrics and
// process memory once per second, and writes artifacts/load/<scenario>.json plus a markdown
// summary. The bots run in the same process, so process-wide figures (managed memory, working
// set, GC counts) include the bots; the tick figures are the server's alone.
public static class LoadRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public static async Task<LoadReport> RunAsync(LoadScenario scenario, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        log ??= static _ => { };
        string repoRoot = LoadPaths.FindRepoRoot();
        string workDirectory = Path.Combine(Path.GetTempPath(), $"dream-load-{scenario.Name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDirectory);
        string databasePath = Path.Combine(workDirectory, "world.sqlite");
        string schemaPath = Path.Combine(repoRoot, "dream", "config", "player-profiles.init.sql");
        // A scenario limit applies to both directions; otherwise the production directional
        // limits (FrameLimitOptions: the server reads 64 KB and writes 8 MB, bots the reverse).
        IMessageFramer serverFramer = scenario.MaxFramePayloadBytes is { } maxPayload
            ? new LengthPrefixedMessageFramer(maxPayload)
            : FrameLimitOptions.Default.CreateServerFramer();
        IMessageFramer botFramer = scenario.MaxFramePayloadBytes is { } botMaxPayload
            ? new LengthPrefixedMessageFramer(botMaxPayload)
            : FrameLimitOptions.Default.CreateClientFramer();
        // Production snapshot interval (2), or DREAM_SNAPSHOT_INTERVAL_TICKS for comparisons.
        ServerConnectionOptions connectionOptions = ServerConnectionOptions.Default.WithEnvironmentOverrides(log);
        log($"snapshot interval {connectionOptions.SnapshotIntervalTicks} ticks");

        try
        {
            WorldSeedResult seed = WorldSeeder.Seed(databasePath, schemaPath, scenario);
            log($"seeded {seed.PlacedCubes} cubes, {seed.RubbleCells} rubble cells, {seed.Enemies} enemies in {seed.SeedMilliseconds:0} ms");

            ForceFullCollection();
            long managedBeforeStartup = GC.GetTotalMemory(forceFullCollection: false);
            Stopwatch startup = Stopwatch.StartNew();
            ServerPersistenceBundle bundle = ServerBootstrap.CreatePersistenceBundle(databasePath, schemaPath);
            DedicatedServerHost host = CreateHost(bundle, scenario.FloorSize);
            startup.Stop();
            WorldSnapshot restored = host.Runtime!.ExportWorldSnapshot();
            StartupResult startupResult = new(
                startup.Elapsed.TotalMilliseconds,
                // Player-placed cubes carry an actor id; raised terrain cells (also y > 0) do not.
                restored.PlacedCubes?.Count(cube => cube.ActorId.HasValue) ?? restored.PlacedCubeCells.Count,
                restored.FracturedCubes?.Count ?? 0,
                host.Runtime.Enemies.ActiveActors.Count,
                GC.GetTotalMemory(forceFullCollection: false) - managedBeforeStartup);
            log($"startup {startupResult.LoadMilliseconds:0} ms, restored {startupResult.RestoredPlacedCubes} cubes, {startupResult.RestoredRubbleCells} rubble cells, {startupResult.Enemies} enemies");

            await using DedicatedServerService service = new(
                host,
                new TcpServerTransport(IPAddress.Loopback, 0),
                new TransportJsonCodec(),
                serverFramer,
                connectionOptions);
            ConnectionErrorLog connectionErrors = new();
            service.ConnectionError += connectionErrors.Add;
            service.Start();
            using CancellationTokenSource serverShutdown = new();
            Task serverTask = Task.Run(() => service.RunAsync(serverShutdown.Token), CancellationToken.None);

            LoadReport report;
            try
            {
                report = await DriveAsync(scenario, service, botFramer, seed, startupResult, databasePath, connectionErrors, log, cancellationToken);
            }
            finally
            {
                serverShutdown.Cancel();
                try
                {
                    await serverTask;
                }
                catch (OperationCanceledException)
                {
                }

                await service.DisposeAsync();
                host.Dispose();
            }

            WriteArtifacts(repoRoot, report);
            return report;
        }
        finally
        {
            // Only this run's pool, so the temp database can be deleted without touching a
            // neighbouring test's open databases.
            SqlitePools.Release(databasePath);
            TryDeleteDirectory(workDirectory);
        }
    }

    // The authoritative host the dedicated server runs. A non-default floor size builds the same
    // stack as RuntimeAuthoritativeSessionHost.CreateDefault with that floor (the production
    // factory always uses WorldDefaults.StartupFloorSize).
    private static DedicatedServerHost CreateHost(ServerPersistenceBundle bundle, int floorSize)
    {
        if (floorSize == WorldDefaults.StartupFloorSize)
        {
            return ServerBootstrap.CreateAuthoritativeHost(bundle);
        }

        WorldSessionRuntime runtime = new(
            floorSize: floorSize,
            playerStartPosition: new Vector3(5f, 1.5f, 5f),
            new SceneEventHub(),
            playerProfiles: bundle.PlayerProfiles,
            worldPersistence: bundle.World,
            combatFormulaService: bundle.FormulaService,
            checkpointWriter: new SqlitePersistenceCheckpointWriter(bundle.Schema),
            persistenceWrites: PersistenceWriteQueue.Background(),
            simulationSeed: bundle.SimulationSeed ?? WorldSessionRuntime.DefaultSimulationSeed);
        ProtocolRouter router = new(
            [
                new ClientHelloHandler(),
                new JoinRequestHandler(),
                new PlayerCommandHandler(),
                new PlayerActionHandler(),
                new WorldResyncRequestHandler()
            ],
            Array.Empty<IClientMessageHandler>());
        return new DedicatedServerHost(new RuntimeAuthoritativeSessionHost(runtime, bundle), router, deferJoinsToTick: true);
    }

    private static async Task<LoadReport> DriveAsync(
        LoadScenario scenario,
        DedicatedServerService service,
        IMessageFramer framer,
        WorldSeedResult seed,
        StartupResult startup,
        string databasePath,
        ConnectionErrorLog connectionErrors,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        int port = service.Port;
        BotClient?[] bots = new BotClient?[scenario.Players];
        List<BotSummary> retiredBots = [];
        int generation = 0;
        int joinRequests = 0;
        int connectFailures = 0;

        async Task<BotClient?> ConnectBotAsync(int index, int botGeneration)
        {
            string sessionId = botGeneration == 0 ? $"load-bot-{index:D3}" : $"load-bot-{index:D3}-g{botGeneration}";
            Interlocked.Increment(ref joinRequests);
            try
            {
                return await BotClient.ConnectAsync(
                    port,
                    sessionId,
                    scenario.Actions,
                    scenario.Seed + index * 7919 + botGeneration,
                    framer,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref connectFailures);
                log($"bot {sessionId} failed to connect: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        Stopwatch connectClock = Stopwatch.StartNew();
        BotClient?[] connected = await Task.WhenAll(Enumerable.Range(0, scenario.Players).Select(i => ConnectBotAsync(i, 0)));
        for (int i = 0; i < connected.Length; i++)
        {
            bots[i] = connected[i];
        }

        // Joins are applied at the next tick; wait for every reply (bounded) before measuring.
        Stopwatch joinWait = Stopwatch.StartNew();
        while (joinWait.Elapsed < TimeSpan.FromSeconds(30) &&
            bots.Any(bot => bot != null && !bot.JoinAccepted && bot.JoinRejectedReason == null && bot.IsConnected))
        {
            foreach (BotClient? bot in bots)
            {
                bot?.Drain();
            }

            await Task.Delay(10, cancellationToken);
        }

        double allJoinedMilliseconds = connectClock.Elapsed.TotalMilliseconds;
        log($"{bots.Count(bot => bot?.JoinAccepted == true)}/{scenario.Players} bots joined in {allJoinedMilliseconds:0} ms");

        // Driver: one thread steps every bot at the command rate, paced like the server tick loop.
        using CancellationTokenSource driverStop = new();
        double frameSeconds = 1d / scenario.CommandRateHz;
        object botsSync = new();
        Thread driver = new(() =>
        {
            using TickPacer pacer = new();
            long next = Stopwatch.GetTimestamp();
            long frameTicks = (long)(frameSeconds * Stopwatch.Frequency);
            while (!driverStop.IsCancellationRequested)
            {
                lock (botsSync)
                {
                    foreach (BotClient? bot in bots)
                    {
                        bot?.Step(frameSeconds);
                    }
                }

                next += frameTicks;
                long now = Stopwatch.GetTimestamp();
                if (next < now - frameTicks * 8)
                {
                    next = now;
                }

                pacer.WaitFor((next - now) / (double)Stopwatch.Frequency, driverStop.Token);
            }
        })
        {
            IsBackground = true,
            Name = "load-bot-driver"
        };

        List<LoadSample> samples = [];
        List<ForcedGcSample> forcedGcSamples = [];
        int initialGen0 = GC.CollectionCount(0);
        int initialGen1 = GC.CollectionCount(1);
        int initialGen2 = GC.CollectionCount(2);
        long peakWorkingSet = 0;
        long dbStartBytes = FileLength(databasePath);
        long walStartBytes = FileLength(databasePath + "-wal");

        service.CaptureMetrics(resetWindow: true);
        ServerMetricsSnapshot runStart = service.CaptureMetrics();
        driver.Start();
        Stopwatch runClock = Stopwatch.StartNew();
        TimeSpan nextSample = TimeSpan.FromSeconds(1);
        TimeSpan nextChurn = scenario.ReconnectInterval ?? TimeSpan.MaxValue;
        TimeSpan nextForcedGc = scenario.EffectiveWarmUp;
        ServerMetricsSnapshot previous = runStart;
        // Steady state starts after the warm-up: the tick distribution is reset there, and rates
        // in the report are measured from there. Short runs that never reach it use the run start.
        ServerMetricsSnapshot steadyStart = runStart;
        TimeSpan steadyStartTime = TimeSpan.Zero;
        bool steadyStarted = false;
        long botSnapshotsAtSteadyStart = 0;
        int churnIndex = 0;
        long churnReconnects = 0;

        try
        {
            while (runClock.Elapsed < scenario.Duration)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TimeSpan now = runClock.Elapsed;
                if (now >= nextChurn)
                {
                    nextChurn += scenario.ReconnectInterval!.Value;
                    int index = churnIndex++ % scenario.Players;
                    BotClient? old;
                    lock (botsSync)
                    {
                        old = bots[index];
                        bots[index] = null;
                    }

                    if (old != null)
                    {
                        retiredBots.Add(BotSummary.From(old));
                        await old.DisposeAsync();
                    }

                    BotClient? replacement = await ConnectBotAsync(index, ++generation);
                    lock (botsSync)
                    {
                        bots[index] = replacement;
                    }

                    churnReconnects++;
                }

                if (now >= nextForcedGc)
                {
                    nextForcedGc = scenario.ForcedGcInterval is { } interval ? now + interval : TimeSpan.MaxValue;
                    forcedGcSamples.Add(ForcedGcSample.Take(now));
                    if (!steadyStarted)
                    {
                        steadyStarted = true;
                        service.TickMetrics.ResetDistribution();
                        steadyStart = service.CaptureMetrics();
                        steadyStartTime = runClock.Elapsed;
                        lock (botsSync)
                        {
                            botSnapshotsAtSteadyStart = bots.Sum(bot => bot?.SnapshotsReceived ?? 0);
                        }
                    }
                }

                if (now >= nextSample)
                {
                    nextSample += TimeSpan.FromSeconds(1);
                    ServerMetricsSnapshot metrics = service.CaptureMetrics(resetWindow: true);
                    LoadSample sample = LoadSample.Take(now, metrics, previous, databasePath);
                    peakWorkingSet = Math.Max(peakWorkingSet, sample.WorkingSetBytes);
                    samples.Add(sample);
                    previous = metrics;
                }

                await Task.Delay(20, cancellationToken);
            }
        }
        finally
        {
            driverStop.Cancel();
            driver.Join(TimeSpan.FromSeconds(10));
        }

        double runSeconds = runClock.Elapsed.TotalSeconds;
        double steadySeconds = Math.Max(1e-3d, (runClock.Elapsed - steadyStartTime).TotalSeconds);
        ServerMetricsSnapshot final = service.CaptureMetrics();
        forcedGcSamples.Add(ForcedGcSample.Take(runClock.Elapsed));

        List<BotSummary> botSummaries = [.. retiredBots];
        foreach (BotClient? bot in bots)
        {
            if (bot != null)
            {
                bot.Drain();
                botSummaries.Add(BotSummary.From(bot));
            }
        }

        foreach (BotClient? bot in bots)
        {
            if (bot != null)
            {
                await bot.DisposeAsync();
            }
        }

        int joined = botSummaries.Count(bot => bot.JoinAccepted);
        double[] joinTimes = botSummaries.Where(bot => bot.JoinMilliseconds.HasValue).Select(bot => bot.JoinMilliseconds!.Value).ToArray();
        long bytesSentDuringRun = final.BytesSent - steadyStart.BytesSent;
        long bytesReceivedDuringRun = final.BytesReceived - steadyStart.BytesReceived;
        // Bots still connected at the end (churned bots' snapshots are in the retired summaries,
        // but only live bots span the steady window).
        long liveBotSnapshots = bots.Sum(bot => bot?.SnapshotsReceived ?? 0);
        int activeClients = Math.Max(1, scenario.Players);
        NetworkResult network = new(
            TotalBytesSent: final.BytesSent,
            TotalFramesSent: final.FramesSent,
            TotalBytesReceived: final.BytesReceived,
            TotalFramesReceived: final.FramesReceived,
            CoalescedSnapshots: final.CoalescedSnapshots,
            RunBytesSentPerSecond: bytesSentDuringRun / steadySeconds,
            RunBytesSentPerSecondPerClient: bytesSentDuringRun / steadySeconds / activeClients,
            RunBytesReceivedPerSecondPerClient: bytesReceivedDuringRun / steadySeconds / activeClients,
            RunFramesSentPerSecondPerClient: (final.FramesSent - steadyStart.FramesSent) / steadySeconds / activeClients,
            BotBytesReceivedTotal: botSummaries.Sum(bot => bot.BytesReceived),
            BotBytesSentTotal: botSummaries.Sum(bot => bot.BytesSent),
            BotSnapshotsPerSecondMean: scenario.ReconnectInterval == null
                ? (liveBotSnapshots - botSnapshotsAtSteadyStart) / steadySeconds / activeClients
                : botSummaries.Sum(bot => bot.SnapshotsReceived) / runSeconds / activeClients);

        GCMemoryInfo gcInfo = GC.GetGCMemoryInfo();
        MemoryResult memory = new(
            ManagedBytesStart: samples.FirstOrDefault()?.ManagedBytes ?? 0,
            ManagedBytesEnd: GC.GetTotalMemory(forceFullCollection: false),
            ForcedGc: forcedGcSamples,
            PostWarmUpForcedGcBytes: forcedGcSamples.FirstOrDefault()?.ManagedBytesAfterFullGc ?? 0,
            FinalForcedGcBytes: forcedGcSamples.LastOrDefault()?.ManagedBytesAfterFullGc ?? 0,
            HeapSizeBytes: gcInfo.HeapSizeBytes,
            TotalCommittedBytes: gcInfo.TotalCommittedBytes,
            FragmentedBytes: gcInfo.FragmentedBytes,
            PauseTimePercentage: gcInfo.PauseTimePercentage,
            PeakWorkingSetBytes: Math.Max(peakWorkingSet, CurrentWorkingSet()),
            Gen0Collections: GC.CollectionCount(0) - initialGen0,
            Gen1Collections: GC.CollectionCount(1) - initialGen1,
            Gen2Collections: GC.CollectionCount(2) - initialGen2);

        TickResult tick = new(
            final.Tick.Tick,
            final.Tick.AdvancePerTick,
            steadySeconds,
            final.Tick.SimulatedTicks - steadyStart.Tick.SimulatedTicks,
            final.Tick.TickedAdvances - steadyStart.Tick.TickedAdvances,
            final.Tick.DroppedTicks - steadyStart.Tick.DroppedTicks,
            (final.Tick.SimulatedTicks - steadyStart.Tick.SimulatedTicks) / steadySeconds,
            (final.Tick.SimulatedTicks - steadyStart.Tick.SimulatedTicks) /
                (double)Math.Max(1L, final.Tick.TickedAdvances - steadyStart.Tick.TickedAdvances),
            final.Tick.MaxAdvanceMilliseconds,
            final.Tick.MaxSnapshotMilliseconds,
            final.Tick.MaxBroadcastMilliseconds,
            samples.Count == 0 ? 0d : samples.Max(sample => sample.TickWindow.P99Milliseconds));

        PersistenceResult persistence = new(
            final.CheckpointWritesCompleted,
            final.CheckpointWritesFailed,
            final.CheckpointsSuperseded,
            final.Tick.CheckpointCaptureTicks,
            final.Tick.MaxCheckpointCaptureTickMilliseconds,
            final.LastCheckpointWriteMilliseconds,
            final.MeanCheckpointWriteMilliseconds,
            final.MaxCheckpointWriteMilliseconds,
            final.PersistenceWritesCompleted,
            final.PersistenceWritesFailed,
            final.Tick.MaxWriteQueueDepth,
            dbStartBytes,
            walStartBytes,
            FileLength(databasePath),
            FileLength(databasePath + "-wal"));

        return new LoadReport(
            scenario.Name,
            DateTime.UtcNow,
            ScenarioSettings.From(scenario),
            EnvironmentInfo.Capture(),
            seed,
            startup,
            new JoinResult(
                scenario.Players,
                joinRequests,
                joined,
                connectFailures,
                botSummaries.Count(bot => bot.JoinRejectedReason != null),
                joinTimes.Length == 0 ? 0d : joinTimes.Average(),
                joinTimes.Length == 0 ? 0d : joinTimes.Max(),
                allJoinedMilliseconds,
                churnReconnects,
                connectionErrors.Count,
                connectionErrors.FirstReasons()),
            runSeconds,
            tick,
            network,
            persistence,
            memory,
            final,
            samples,
            botSummaries);
    }

    private static void WriteArtifacts(string repoRoot, LoadReport report)
    {
        string directory = Path.Combine(repoRoot, "artifacts", "load");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"{report.Scenario}.json"), JsonSerializer.Serialize(report, JsonOptions));
        File.WriteAllText(Path.Combine(directory, $"{report.Scenario}.md"), LoadReportMarkdown.Render(report));
    }

    internal static void ForceFullCollection()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    internal static long CurrentWorkingSet()
    {
        using Process process = Process.GetCurrentProcess();
        return process.WorkingSet64;
    }

    internal static long FileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
            return -1;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

internal static class LoadPaths
{
    // The repository root: the nearest ancestor of the test output holding dream and dream.Tests.
    public static string FindRepoRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "dream")) &&
                Directory.Exists(Path.Combine(current.FullName, "dream.Tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}

public sealed record LoadReport(
    string Scenario,
    DateTime FinishedUtc,
    ScenarioSettings Settings,
    EnvironmentInfo Environment,
    WorldSeedResult Seed,
    StartupResult Startup,
    JoinResult Join,
    double RunSeconds,
    TickResult Tick,
    NetworkResult Network,
    PersistenceResult Persistence,
    MemoryResult Memory,
    ServerMetricsSnapshot FinalServerMetrics,
    IReadOnlyList<LoadSample> Samples,
    IReadOnlyList<BotSummary> Bots);

public sealed record ScenarioSettings(
    string Name,
    int Players,
    int Enemies,
    int Cubes,
    int RubbleCells,
    int FloorSize,
    double DurationSeconds,
    double? ReconnectIntervalSeconds,
    double WarmUpSeconds,
    int CommandRateHz,
    int? MaxFramePayloadBytes,
    LoadActionMix Actions)
{
    public static ScenarioSettings From(LoadScenario scenario) => new(
        scenario.Name,
        scenario.Players,
        scenario.Enemies,
        scenario.Cubes,
        scenario.RubbleCells,
        scenario.FloorSize,
        scenario.Duration.TotalSeconds,
        scenario.ReconnectInterval?.TotalSeconds,
        scenario.EffectiveWarmUp.TotalSeconds,
        scenario.CommandRateHz,
        scenario.MaxFramePayloadBytes,
        scenario.Actions);
}

public sealed record EnvironmentInfo(
    string MachineName,
    int ProcessorCount,
    string OsDescription,
    string Framework,
    bool ServerGc,
    string GcLatencyMode,
    bool DebugBuild)
{
    public static EnvironmentInfo Capture() => new(
        System.Environment.MachineName,
        System.Environment.ProcessorCount,
        RuntimeInformation.OSDescription,
        RuntimeInformation.FrameworkDescription,
        GCSettings.IsServerGC,
        GCSettings.LatencyMode.ToString(),
#if DEBUG
        DebugBuild: true);
#else
        DebugBuild: false);
#endif
}

// LoadMilliseconds covers opening the database (schema, seed, repositories) and constructing the
// host, which restores every persisted cube, rubble cell and enemy.
public sealed record StartupResult(
    double LoadMilliseconds,
    int RestoredPlacedCubes,
    int RestoredRubbleCells,
    int Enemies,
    long ManagedBytesAllocatedByStartup);

public sealed record JoinResult(
    int Bots,
    int JoinRequests,
    int Joined,
    int ConnectFailures,
    int Rejected,
    double MeanJoinMilliseconds,
    double MaxJoinMilliseconds,
    double AllInitialBotsJoinedMilliseconds,
    long ChurnReconnects,
    // Connections the server closed on an error (DedicatedServerService.ConnectionError).
    int ServerConnectionErrors,
    IReadOnlyList<string> ServerConnectionErrorReasons);

internal sealed class ConnectionErrorLog
{
    private const int MaxReasons = 5;
    private readonly object _sync = new();
    private readonly List<string> _reasons = [];
    private int _count;

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _count;
            }
        }
    }

    public void Add(ServerConnectionError error)
    {
        lock (_sync)
        {
            _count++;
            if (_reasons.Count < MaxReasons)
            {
                _reasons.Add($"{error.Kind}: {error.Reason}");
            }
        }
    }

    public IReadOnlyList<string> FirstReasons()
    {
        lock (_sync)
        {
            return _reasons.ToArray();
        }
    }
}

// Steady state (after the warm-up). Each histogram sample is one tick-loop iteration: advance
// (TicksPerAdvance simulation ticks, usually 1) plus snapshot and broadcast. TicksPerAdvance above
// 1 means the loop fell behind and caught up with back-to-back ticks.
public sealed record TickResult(
    LatencySummary SteadyState,
    // Simulation cost of one tick (an iteration's advance time divided by its ticks).
    LatencySummary AdvancePerTick,
    double SteadySeconds,
    long SimulatedTicks,
    long TickedAdvances,
    long DroppedTicks,
    double TicksPerSecond,
    double TicksPerAdvance,
    double MaxAdvanceMilliseconds,
    double MaxSnapshotMilliseconds,
    double MaxBroadcastMilliseconds,
    double WorstSecondP99Milliseconds);

public sealed record NetworkResult(
    long TotalBytesSent,
    long TotalFramesSent,
    long TotalBytesReceived,
    long TotalFramesReceived,
    long CoalescedSnapshots,
    double RunBytesSentPerSecond,
    double RunBytesSentPerSecondPerClient,
    double RunBytesReceivedPerSecondPerClient,
    double RunFramesSentPerSecondPerClient,
    long BotBytesReceivedTotal,
    long BotBytesSentTotal,
    double BotSnapshotsPerSecondMean);

public sealed record PersistenceResult(
    long CheckpointWritesCompleted,
    long CheckpointWritesFailed,
    long CheckpointsSuperseded,
    long CheckpointCaptureTicks,
    double MaxCheckpointCaptureTickMilliseconds,
    double LastCheckpointWriteMilliseconds,
    double MeanCheckpointWriteMilliseconds,
    double MaxCheckpointWriteMilliseconds,
    long PersistenceWritesCompleted,
    long PersistenceWritesFailed,
    int MaxWriteQueueDepth,
    long DatabaseBytesStart,
    long WalBytesStart,
    long DatabaseBytesEnd,
    long WalBytesEnd);

public sealed record MemoryResult(
    long ManagedBytesStart,
    long ManagedBytesEnd,
    IReadOnlyList<ForcedGcSample> ForcedGc,
    long PostWarmUpForcedGcBytes,
    long FinalForcedGcBytes,
    long HeapSizeBytes,
    long TotalCommittedBytes,
    long FragmentedBytes,
    double PauseTimePercentage,
    long PeakWorkingSetBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections);

public sealed record ForcedGcSample(double Seconds, long ManagedBytesAfterFullGc, long WorkingSetBytes)
{
    public static ForcedGcSample Take(TimeSpan at)
    {
        LoadRunner.ForceFullCollection();
        return new ForcedGcSample(at.TotalSeconds, GC.GetTotalMemory(forceFullCollection: false), LoadRunner.CurrentWorkingSet());
    }
}

public sealed record LoadSample(
    double Seconds,
    LatencySummary TickWindow,
    long SimulatedTicks,
    long DroppedTicks,
    int Connections,
    int JoinedConnections,
    long BytesSentDelta,
    long FramesSentDelta,
    long BytesReceivedDelta,
    long CoalescedSnapshotsDelta,
    int WriteQueueDepth,
    long CheckpointWritesCompleted,
    double LastCheckpointWriteMilliseconds,
    long ManagedBytes,
    long HeapSizeBytes,
    long TotalCommittedBytes,
    long WorkingSetBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    long DatabaseBytes,
    long WalBytes)
{
    public static LoadSample Take(TimeSpan at, ServerMetricsSnapshot metrics, ServerMetricsSnapshot previous, string databasePath)
    {
        GCMemoryInfo gcInfo = GC.GetGCMemoryInfo();
        return new LoadSample(
            at.TotalSeconds,
            metrics.Tick.TickWindow,
            metrics.Tick.SimulatedTicks - previous.Tick.SimulatedTicks,
            metrics.Tick.DroppedTicks - previous.Tick.DroppedTicks,
            metrics.Connections,
            metrics.JoinedConnections,
            metrics.BytesSent - previous.BytesSent,
            metrics.FramesSent - previous.FramesSent,
            metrics.BytesReceived - previous.BytesReceived,
            metrics.CoalescedSnapshots - previous.CoalescedSnapshots,
            metrics.Tick.WriteQueueDepth,
            metrics.CheckpointWritesCompleted,
            metrics.LastCheckpointWriteMilliseconds,
            GC.GetTotalMemory(forceFullCollection: false),
            gcInfo.HeapSizeBytes,
            gcInfo.TotalCommittedBytes,
            LoadRunner.CurrentWorkingSet(),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            LoadRunner.FileLength(databasePath),
            LoadRunner.FileLength(databasePath + "-wal"));
    }
}

public sealed record BotSummary(
    string SessionId,
    bool JoinAccepted,
    string? JoinRejectedReason,
    double? JoinMilliseconds,
    long BytesSent,
    long BytesReceived,
    long CommandsSent,
    long MessagesReceived,
    long SnapshotsReceived,
    long ActionResultsReceived,
    long BuildsRequested,
    long AttacksRequested,
    long ActionsQueued)
{
    public static BotSummary From(BotClient bot) => new(
        bot.SessionId,
        bot.JoinAccepted,
        bot.JoinRejectedReason,
        bot.JoinMilliseconds,
        bot.BytesSent,
        bot.BytesReceived,
        bot.CommandsSent,
        bot.MessagesReceived,
        bot.SnapshotsReceived,
        bot.ActionResultsReceived,
        bot.BuildsRequested,
        bot.AttacksRequested,
        bot.ActionsQueued);
}

internal static class LoadReportMarkdown
{
    public static string Render(LoadReport report)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        StringBuilder md = new();
        md.AppendLine(c, $"# Load run: {report.Scenario}");
        md.AppendLine();
        md.AppendLine(c, $"- Finished (UTC): {report.FinishedUtc:yyyy-MM-dd HH:mm:ss}");
        md.AppendLine(c, $"- Machine: {report.Environment.MachineName}, {report.Environment.ProcessorCount} logical CPUs, {report.Environment.OsDescription}, {report.Environment.Framework}, GC mode: {(report.Environment.ServerGc ? "SERVER GC (NOT production: production is workstation concurrent, numbers are not comparable)" : "workstation concurrent (matches production)")} / latency {report.Environment.GcLatencyMode}, {(report.Environment.DebugBuild ? "Debug" : "Release")} build");
        md.AppendLine(c, $"- Scenario: {report.Settings.Players} bots, {report.Settings.Cubes} cubes, {report.Settings.RubbleCells} rubble cells, {report.Settings.Enemies} enemies, floor {report.Settings.FloorSize}, {report.RunSeconds:0.0} s run");
        md.AppendLine();
        md.AppendLine("| Metric | Value |");
        md.AppendLine("| --- | --- |");
        Row(md, "Tick-loop iteration p50 / p95 / p99 / max (ms)", $"{report.Tick.SteadyState.P50Milliseconds:0.000} / {report.Tick.SteadyState.P95Milliseconds:0.000} / {report.Tick.SteadyState.P99Milliseconds:0.000} / {report.Tick.SteadyState.MaxMilliseconds:0.000}");
        Row(md, "Tick mean (ms)", $"{report.Tick.SteadyState.MeanMilliseconds:0.000}");
        Row(md, "Advance per simulated tick p50 / p95 / p99 / max (ms)", $"{report.Tick.AdvancePerTick.P50Milliseconds:0.000} / {report.Tick.AdvancePerTick.P95Milliseconds:0.000} / {report.Tick.AdvancePerTick.P99Milliseconds:0.000} / {report.Tick.AdvancePerTick.MaxMilliseconds:0.000}");
        Row(md, "Worst one-second p99 (ms)", $"{report.Tick.WorstSecondP99Milliseconds:0.000}");
        Row(md, "Max advance / snapshot / broadcast (ms)", $"{report.Tick.MaxAdvanceMilliseconds:0.000} / {report.Tick.MaxSnapshotMilliseconds:0.000} / {report.Tick.MaxBroadcastMilliseconds:0.000}");
        Row(md, "Simulated ticks/s, ticks per loop iteration, dropped ticks", $"{report.Tick.TicksPerSecond:0.0}, {report.Tick.TicksPerAdvance:0.00}, {report.Tick.DroppedTicks}");
        Row(md, "Startup load (ms)", $"{report.Startup.LoadMilliseconds:0} (restored {report.Startup.RestoredPlacedCubes} cubes, {report.Startup.RestoredRubbleCells} rubble cells, {report.Startup.Enemies} enemies)");
        Row(md, "Joined / requested", $"{report.Join.Joined} / {report.Join.JoinRequests} (connect failures {report.Join.ConnectFailures}, rejected {report.Join.Rejected})");
        Row(md, "Server connection errors", report.Join.ServerConnectionErrors == 0
            ? "0"
            : $"{report.Join.ServerConnectionErrors} (first: {string.Join("; ", report.Join.ServerConnectionErrorReasons)})");
        Row(md, "Join mean / max (ms)", $"{report.Join.MeanJoinMilliseconds:0} / {report.Join.MaxJoinMilliseconds:0}");
        Row(md, "Server -> client bytes/s per client", $"{report.Network.RunBytesSentPerSecondPerClient:0}");
        Row(md, "Client -> server bytes/s per client", $"{report.Network.RunBytesReceivedPerSecondPerClient:0}");
        Row(md, "Frames/s per client, coalesced snapshots", $"{report.Network.RunFramesSentPerSecondPerClient:0.0}, {report.Network.CoalescedSnapshots}");
        Row(md, "Bot snapshots/s (mean)", $"{report.Network.BotSnapshotsPerSecondMean:0.0}");
        Row(md, "Checkpoints: writes, failed, mean / max write ms", $"{report.Persistence.CheckpointWritesCompleted}, {report.Persistence.CheckpointWritesFailed}, {report.Persistence.MeanCheckpointWriteMilliseconds:0.0} / {report.Persistence.MaxCheckpointWriteMilliseconds:0.0}");
        Row(md, "Checkpoint capture tick max (ms)", $"{report.Persistence.MaxCheckpointCaptureTickMilliseconds:0.000}");
        Row(md, "Persistence writes, failed, max queue depth", $"{report.Persistence.PersistenceWritesCompleted}, {report.Persistence.PersistenceWritesFailed}, {report.Persistence.MaxWriteQueueDepth}");
        Row(md, "DB / WAL bytes (start -> end)", $"{report.Persistence.DatabaseBytesStart} / {report.Persistence.WalBytesStart} -> {report.Persistence.DatabaseBytesEnd} / {report.Persistence.WalBytesEnd}");
        Row(md, "Managed MB after full GC (post warm-up -> end)", $"{report.Memory.PostWarmUpForcedGcBytes / 1048576d:0.0} -> {report.Memory.FinalForcedGcBytes / 1048576d:0.0}");
        Row(md, "Peak working set (MB)", $"{report.Memory.PeakWorkingSetBytes / 1048576d:0.0}");
        Row(md, "GC gen0 / gen1 / gen2 during run", $"{report.Memory.Gen0Collections} / {report.Memory.Gen1Collections} / {report.Memory.Gen2Collections}");
        md.AppendLine();
        md.AppendLine(c, $"Tick and rate figures cover the steady state ({report.Tick.SteadySeconds:0.0} s after a {report.Settings.WarmUpSeconds:0.0} s warm-up); the per-second samples in the JSON cover the whole run. Process-wide memory and GC figures include the in-process bots; tick figures are the server's alone.");
        return md.ToString();

        static void Row(StringBuilder builder, string name, string value)
        {
            builder.Append("| ").Append(name).Append(" | ").Append(value).AppendLine(" |");
        }
    }
}
