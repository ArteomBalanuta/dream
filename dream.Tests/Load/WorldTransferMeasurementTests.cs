using System.Diagnostics;
using System.Net;
using Dream.Client.Networking;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Load;

// SCALE-1 measurement (DREAM_LOAD_TEST=1, Release): joins into a seeded world over loopback TCP
// with the production client session, one at a time, while the test runs the server's tick at
// 64 Hz. Reports per join: connect-to-JoinAccepted milliseconds, the bytes the server wrote to
// that connection until the join was complete, and the longest tick while the join was being
// applied against the median tick before any join (the join's tick stall).
[Collection(TimingSensitiveCollection.Name)]
public sealed class WorldTransferMeasurementTests
{
    private const int Joins = 4;

    private readonly ITestOutputHelper _output;

    public WorldTransferMeasurementTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [LoadFact]
    public Task World10k_JoinTransfer()
    {
        return MeasureAsync(LoadScenario.World10k);
    }

    [LoadFact]
    public Task World50k_JoinTransfer()
    {
        return MeasureAsync(LoadScenario.World10k with { Name = "World50k", Cubes = 50_000 });
    }

    private async Task MeasureAsync(LoadScenario scenario)
    {
        string root = LoadPaths.FindRepoRoot();
        string work = Path.Combine(Path.GetTempPath(), $"dream-scale1-{scenario.Name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        string databasePath = Path.Combine(work, "world.sqlite");
        string schemaPath = Path.Combine(root, "dream", "config", "player-profiles.init.sql");
        try
        {
            WorldSeedResult seed = WorldSeeder.Seed(databasePath, schemaPath, scenario);
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
                simulationSeed: bundle.SimulationSeed ?? WorldSessionRuntime.DefaultSimulationSeed);
            ProtocolRouter router = new(
                [new ClientHelloHandler(), new JoinRequestHandler(), new PlayerCommandHandler(), new PlayerActionHandler(), new WorldResyncRequestHandler()],
                Array.Empty<IClientMessageHandler>());
            DedicatedServerHost host = new(new RuntimeAuthoritativeSessionHost(runtime, bundle), router, deferJoinsToTick: true);
            DedicatedServerService service = new(
                host,
                new TcpServerTransport(IPAddress.Loopback, 0),
                new TransportJsonCodec(),
                FrameLimitOptions.Default.CreateServerFramer(),
                ServerConnectionOptions.Default);
            List<string> errors = [];
            service.ConnectionError += error =>
            {
                lock (errors)
                {
                    errors.Add($"{error.Kind}: {error.Reason}");
                }
            };
            service.Start();
            _output.WriteLine($"{scenario.Name}: seeded {seed.PlacedCubes} cubes, {seed.RubbleCells} rubble cells, {seed.FracturePieces} pieces");

            MeasureExport(runtime);
            TickRecorder ticks = new();
            using CancellationTokenSource stop = new();
            Task tickLoop = Task.Run(() => TickLoopAsync(service, ticks, stop.Token));
            try
            {
                await Task.Delay(1500);
                double medianTick = ticks.Median();
                List<string> rows = [];
                List<double> joinMs = [];
                List<long> joinBytes = [];
                List<double> stalls = [];
                for (int join = 0; join < Joins; join++)
                {
                    int tickMark = ticks.Count;
                    Task<ServerConnection> accept = service.AcceptOnThreadPoolAsync();
                    long started = Stopwatch.GetTimestamp();
                    RemoteClientSession session = await RemoteClientSession.ConnectAsync(
                        new RemoteClientOptions("127.0.0.1", service.Port, $"measure-{join}", $"measure-{join}", "scale1-measure"),
                        framer: FrameLimitOptions.Default.CreateClientFramer());
                    ServerConnection connection = await accept.WaitAsync(TimeSpan.FromSeconds(30));
                    JoinAcceptedMessage? accepted = null;
                    DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
                    while (accepted == null)
                    {
                        Assert.True(
                            DateTime.UtcNow < deadline && session.IsConnected,
                            $"join {join} never completed ({session.DisconnectReason}; server: {connection.CloseReason}, {string.Join("; ", errors)})");
                        while (session.TryDequeueInbound(out IServerToClientMessage? message))
                        {
                            if (message is JoinAcceptedMessage joinAccepted)
                            {
                                accepted = joinAccepted;
                            }
                        }

                        if (accepted == null)
                        {
                            await Task.Delay(1);
                        }
                    }

                    double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    long bytes = connection.BytesSent;
                    double maxTick = ticks.MaxSince(tickMark);
                    Assert.NotNull(accepted.InitialWorld);
                    int cubes = accepted.InitialWorld!.Value.PlacedCubes?.Count ?? 0;
                    int rubble = accepted.InitialWorld!.Value.FracturedCubes?.Count ?? 0;
                    joinMs.Add(ms);
                    joinBytes.Add(bytes);
                    stalls.Add(maxTick - medianTick);
                    rows.Add($"| {join} | {ms:0.0} | {bytes:N0} | {cubes} / {rubble} | {maxTick:0.00} (median {medianTick:0.00}) |");
                    await session.DisposeAsync();
                    await Task.Delay(300);
                }

                _output.WriteLine("| Join | Connect to JoinAccepted ms | Server bytes to the connection | Cubes / rubble received | Max tick ms during join |");
                _output.WriteLine("| --- | --- | --- | --- | --- |");
                foreach (string row in rows)
                {
                    _output.WriteLine(row);
                }

                _output.WriteLine(
                    $"{scenario.Name} summary: join ms mean {joinMs.Average():0.0} max {joinMs.Max():0.0}; " +
                    $"bytes per join mean {joinBytes.Average():N0}; tick stall over median mean {stalls.Average():0.00} max {stalls.Max():0.00} ms");
            }
            finally
            {
                stop.Cancel();
                try
                {
                    await tickLoop;
                }
                catch (OperationCanceledException)
                {
                }

                await service.DisposeAsync();
                runtime.WriteCheckpointOnDispose = false;
                host.Dispose();
            }
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

    // The world export the join and resync run on the tick, and the encode of a JoinAccepted that
    // carries the whole world in one frame (the connection writer's work), each measured on its own
    // before the server ticks.
    private void MeasureExport(WorldSessionRuntime runtime)
    {
        TransportJsonCodec codec = new();
        List<double> exportMs = [];
        List<long> exportBytes = [];
        List<double> encodeMs = [];
        int encodedBytes = 0;
        for (int run = 0; run < 6; run++)
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            WorldSnapshot world = runtime.ExportWorldSnapshot();
            double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            JoinAcceptedMessage message = new("measure", 1, runtime.CurrentServerTick, world, InitialWorldMutationSequence: world.MutationSequence);
            started = Stopwatch.GetTimestamp();
            byte[] encoded = codec.Encode(new TransportEnvelope(TransportEnvelope.CurrentProtocolVersion, message.MessageKind, 1), message);
            double encode = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (run > 0)
            {
                exportMs.Add(ms);
                exportBytes.Add(bytes);
                encodeMs.Add(encode);
            }

            encodedBytes = encoded.Length;
        }

        _output.WriteLine(
            $"ExportWorldSnapshot (no release): {exportMs.Average():0.00} ms mean, {exportMs.Max():0.00} ms max, {exportBytes.Average():N0} B allocated; " +
            $"single-frame JoinAccepted encode {encodeMs.Average():0.00} ms, {encodedBytes:N0} B");

        // SCALE-1's join path: the tick captures into pooled arrays and the connection writer
        // releases them after the last chunk, so a warm pool serves every later join.
        List<double> captureMs = [];
        List<long> captureBytes = [];
        for (int run = 0; run < 6; run++)
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            WorldSnapshotCapture capture = runtime.CaptureWorldSnapshot();
            double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            capture.Release();
            if (run > 0)
            {
                captureMs.Add(ms);
                captureBytes.Add(bytes);
            }
        }

        _output.WriteLine(
            $"Pooled capture on the tick (released after send): {captureMs.Average():0.00} ms mean, {captureMs.Max():0.00} ms max, {captureBytes.Average():N0} B allocated");
    }

    private static async Task TickLoopAsync(DedicatedServerService service, TickRecorder ticks, CancellationToken cancellationToken)
    {
        Stopwatch clock = Stopwatch.StartNew();
        double next = 0d;
        while (!cancellationToken.IsCancellationRequested)
        {
            long started = Stopwatch.GetTimestamp();
            await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds, cancellationToken);
            ticks.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            next += SimulationClock.FixedDeltaSeconds * 1000d;
            double wait = next - clock.Elapsed.TotalMilliseconds;
            if (wait > 1d)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(wait), cancellationToken);
            }
            else if (wait < -250d)
            {
                next = clock.Elapsed.TotalMilliseconds;
            }
        }
    }

    private sealed class TickRecorder
    {
        private readonly List<double> _ticks = [];

        public int Count
        {
            get
            {
                lock (_ticks)
                {
                    return _ticks.Count;
                }
            }
        }

        public void Add(double ms)
        {
            lock (_ticks)
            {
                _ticks.Add(ms);
            }
        }

        public double Median()
        {
            lock (_ticks)
            {
                double[] sorted = [.. _ticks];
                Array.Sort(sorted);
                return sorted.Length == 0 ? 0d : sorted[sorted.Length / 2];
            }
        }

        public double MaxSince(int index)
        {
            lock (_ticks)
            {
                double max = 0d;
                for (int i = index; i < _ticks.Count; i++)
                {
                    max = Math.Max(max, _ticks[i]);
                }

                return max;
            }
        }
    }
}
