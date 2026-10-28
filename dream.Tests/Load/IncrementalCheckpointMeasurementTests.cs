using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Dream.Multiplayer.Protocol;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Load;

// SCALE-4 measurement (DREAM_LOAD_TEST=1, Release): the checkpoint capture on the tick and the
// checkpoint write, with a full checkpoint every capture (what every checkpoint was before
// SCALE-4) against incremental ones, on the seeded Smoke8 and World10k worlds. No bots: the
// dedicated host is driven tick by tick, and every simulated second a few cubes are damaged, so
// each incremental checkpoint has some changed rows. Reports per capture kind: the milliseconds
// and bytes the capture tick costs over a median tick, rows per write and write milliseconds.
[Collection(TimingSensitiveCollection.Name)]
public sealed class IncrementalCheckpointMeasurementTests
{
    private const int SimulatedSeconds = 30;
    private const int TicksPerSecond = 64;
    private const int ChangedCubesPerSecond = 20;

    private readonly ITestOutputHelper _output;

    public IncrementalCheckpointMeasurementTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [LoadFact]
    public void Smoke8_FullVersusIncrementalCheckpoints()
    {
        Measure(LoadScenario.Smoke8);
    }

    [LoadFact]
    public void World10k_FullVersusIncrementalCheckpoints()
    {
        Measure(LoadScenario.World10k);
    }

    private void Measure(LoadScenario scenario)
    {
        CheckpointRun full = Run(scenario, fullCheckpointInterval: 1);
        CheckpointRun incremental = Run(scenario, WorldSessionRuntime.DefaultFullCheckpointInterval);
        _output.WriteLine($"{scenario.Name}, {SimulatedSeconds} s simulated, {ChangedCubesPerSecond} cubes damaged per second, median tick {full.MedianTickMs:0.000} / {incremental.MedianTickMs:0.000} ms");
        _output.WriteLine("| Run | Capture kind | Count | Capture tick extra ms mean / max | Capture tick extra bytes mean / max | Rows per write | Write ms mean / max |");
        _output.WriteLine("| --- | --- | --- | --- | --- | --- | --- |");
        _output.WriteLine(full.Full.ToRow("full every capture", "full"));
        _output.WriteLine(incremental.Full.ToRow("incremental", "full (first / sweep)"));
        _output.WriteLine(incremental.Incremental.ToRow("incremental", "incremental"));
        Assert.Equal(0, full.FailedWrites);
        Assert.Equal(0, incremental.FailedWrites);
        Assert.True(incremental.Incremental.Count > 0);
    }

    private static CheckpointRun Run(LoadScenario scenario, int fullCheckpointInterval)
    {
        string root = LoadPaths.FindRepoRoot();
        string work = Path.Combine(Path.GetTempPath(), $"dream-scale4-{scenario.Name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        string databasePath = Path.Combine(work, "world.sqlite");
        string schemaPath = Path.Combine(root, "dream", "config", "player-profiles.init.sql");
        try
        {
            WorldSeeder.Seed(databasePath, schemaPath, scenario);
            ServerPersistenceBundle bundle = ServerBootstrap.CreatePersistenceBundle(databasePath, schemaPath);
            WorldSessionRuntime runtime = new(
                floorSize: scenario.FloorSize,
                playerStartPosition: new Vector3(5f, 1.5f, 5f),
                new SceneEventHub(),
                playerProfiles: bundle.PlayerProfiles,
                worldPersistence: bundle.World,
                combatFormulaService: bundle.FormulaService,
                checkpointWriter: new SqlitePersistenceCheckpointWriter(bundle.Schema),
                persistenceWrites: PersistenceWriteQueue.Background(),
                simulationSeed: bundle.SimulationSeed ?? WorldSessionRuntime.DefaultSimulationSeed)
            {
                FullCheckpointInterval = fullCheckpointInterval
            };
            ProtocolRouter router = new(
                [new ClientHelloHandler(), new JoinRequestHandler(), new PlayerCommandHandler(), new PlayerActionHandler()],
                Array.Empty<IClientMessageHandler>());
            using DedicatedServerHost host = new(new RuntimeAuthoritativeSessionHost(runtime, bundle), router, deferJoinsToTick: true);
            BackgroundCheckpointWriter writes = host.CheckpointWrites!;
            WorldObjectRecord[] cubes = GetWorldObjects(runtime).GetCubeRecords()
                .Where(record => record.SourceObject is Cube)
                .OrderBy(record => record.Actor.Id)
                .ToArray();
            Random random = new(scenario.Seed);

            List<double> tickMs = [];
            List<CaptureSample> captures = [];
            long[] tickBytes = new long[SimulatedSeconds * TicksPerSecond];
            for (int tick = 0; tick < SimulatedSeconds * TicksPerSecond; tick++)
            {
                if (tick % TicksPerSecond == 0)
                {
                    for (int i = 0; i < ChangedCubesPerSecond && cubes.Length > 0; i++)
                    {
                        WorldObjectRecord record = cubes[random.Next(cubes.Length)];
                        if (record.Health.CurrentHealth > 2)
                        {
                            record.ApplyDamage(1);
                        }
                    }
                }

                long enqueued = writes.EnqueuedCheckpoints;
                long fullCaptures = runtime.FullCheckpointCaptures;
                long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
                long started = Stopwatch.GetTimestamp();
                host.AdvanceTime(SimulationClock.FixedDeltaSeconds);
                double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                long bytes = GC.GetAllocatedBytesForCurrentThread() - bytesBefore;
                tickBytes[tick] = bytes;
                if (writes.EnqueuedCheckpoints != enqueued)
                {
                    // Wait for this checkpoint's write (outside the measured tick), so each write
                    // is measured on its own rather than merged with the next one.
                    Assert.True(writes.Flush(TimeSpan.FromSeconds(60)));
                    captures.Add(new CaptureSample(
                        runtime.FullCheckpointCaptures != fullCaptures,
                        ms,
                        bytes,
                        writes.LastWriteRows,
                        writes.LastWriteMilliseconds));
                }
                else
                {
                    tickMs.Add(ms);
                }
            }

            Assert.True(writes.Flush(TimeSpan.FromSeconds(60)));
            tickMs.Sort();
            double median = tickMs.Count == 0 ? 0d : tickMs[tickMs.Count / 2];
            long medianBytes = tickBytes.OrderBy(value => value).ElementAt(tickBytes.Length / 2);
            CheckpointRun run = new(
                Summarize(captures.Where(sample => sample.Full).ToArray(), median, medianBytes),
                Summarize(captures.Where(sample => !sample.Full).ToArray(), median, medianBytes),
                median,
                writes.FailedWrites);
            runtime.WriteCheckpointOnDispose = false;
            return run;
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static KindSummary Summarize(CaptureSample[] samples, double medianMs, long medianBytes)
    {
        if (samples.Length == 0)
        {
            return new KindSummary(0, 0d, 0d, 0d, 0L, 0d, 0d, 0d);
        }

        return new KindSummary(
            samples.Length,
            samples.Average(sample => sample.TickMs - medianMs),
            samples.Max(sample => sample.TickMs - medianMs),
            samples.Average(sample => (double)(sample.TickBytes - medianBytes)),
            samples.Max(sample => sample.TickBytes - medianBytes),
            samples.Average(sample => (double)sample.Rows),
            samples.Average(sample => sample.WriteMs),
            samples.Max(sample => sample.WriteMs));
    }

    private static WorldObjectRegistry GetWorldObjects(WorldSessionRuntime runtime)
    {
        FieldInfo field = typeof(WorldSessionRuntime).GetField("_worldObjects", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (WorldObjectRegistry)field.GetValue(runtime)!;
    }

    private readonly record struct CaptureSample(bool Full, double TickMs, long TickBytes, long Rows, double WriteMs);

    private sealed record CheckpointRun(KindSummary Full, KindSummary Incremental, double MedianTickMs, long FailedWrites);

    private sealed record KindSummary(
        int Count,
        double ExtraMsMean,
        double ExtraMsMax,
        double ExtraBytesMean,
        long ExtraBytesMax,
        double RowsPerWrite,
        double WriteMsMean,
        double WriteMsMax)
    {
        public string ToRow(string run, string kind)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"| {run} | {kind} | {Count} | {ExtraMsMean:0.000} / {ExtraMsMax:0.000} | {ExtraBytesMean:0} / {ExtraBytesMax} | " +
                $"{RowsPerWrite:0.0} | {WriteMsMean:0.00} / {WriteMsMax:0.00} |");
        }
    }
}
