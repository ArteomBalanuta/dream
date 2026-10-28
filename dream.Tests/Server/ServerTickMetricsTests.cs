using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Multiplayer.Transport;
using Dream.Server;
using Dream.Server.Persistence;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using Dream.Tests.Utilities;

namespace Dream.Tests.Server;

public class ServerTickMetricsTests
{
    private readonly ITestOutputHelper _output;

    public ServerTickMetricsTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Histogram_Empty_ReportsZeros()
    {
        LatencyHistogram histogram = new();

        LatencySummary summary = histogram.Summarize();

        Assert.Equal(0, summary.Count);
        Assert.Equal(0d, summary.P50Milliseconds);
        Assert.Equal(0d, summary.P99Milliseconds);
        Assert.Equal(0d, summary.MaxMilliseconds);
    }

    [Fact]
    public void Histogram_SingleValue_ReadsBackExactlyForEveryPercentile()
    {
        LatencyHistogram histogram = new();
        histogram.Record(3.7d);

        Assert.Equal(3.7d, histogram.Percentile(0d));
        Assert.Equal(3.7d, histogram.Percentile(0.5d));
        Assert.Equal(3.7d, histogram.Percentile(0.99d));
        Assert.Equal(3.7d, histogram.MaxMilliseconds);
        Assert.Equal(3.7d, histogram.MeanMilliseconds, 9);
    }

    [Fact]
    public void Histogram_UniformOneToHundred_PercentilesWithinBucketResolution()
    {
        LatencyHistogram histogram = new();
        for (int i = 1; i <= 100; i++)
        {
            histogram.Record(i);
        }

        // Nearest rank: p50 is the 50th sample, p95 the 95th, p99 the 99th. A bucket bound is at
        // most BucketGrowth above the true value and never below it.
        AssertWithinBucket(50d, histogram.Percentile(0.50d));
        AssertWithinBucket(95d, histogram.Percentile(0.95d));
        AssertWithinBucket(99d, histogram.Percentile(0.99d));
        Assert.Equal(100d, histogram.Percentile(1d));
        Assert.Equal(100d, histogram.MaxMilliseconds);
        Assert.Equal(1d, histogram.MinMilliseconds);
        Assert.Equal(50.5d, histogram.MeanMilliseconds, 9);
        Assert.Equal(100, histogram.Count);
    }

    [Fact]
    public void Histogram_TailOutlier_ShowsOnlyAtTheTail()
    {
        LatencyHistogram histogram = new();
        for (int i = 0; i < 990; i++)
        {
            histogram.Record(0.5d);
        }

        for (int i = 0; i < 10; i++)
        {
            histogram.Record(40d);
        }

        AssertWithinBucket(0.5d, histogram.Percentile(0.50d));
        AssertWithinBucket(0.5d, histogram.Percentile(0.95d));
        AssertWithinBucket(0.5d, histogram.Percentile(0.99d));
        AssertWithinBucket(40d, histogram.Percentile(0.995d));
        Assert.Equal(40d, histogram.MaxMilliseconds);
    }

    [Fact]
    public void Histogram_OutOfRangeValues_AreClampedIntoEdgeBuckets()
    {
        LatencyHistogram histogram = new();
        histogram.Record(-1d);
        histogram.Record(double.NaN);
        histogram.Record(0.000_1d);
        histogram.Record(60_000d);

        Assert.Equal(4, histogram.Count);
        Assert.Equal(0d, histogram.MinMilliseconds);
        Assert.Equal(60_000d, histogram.Percentile(1d));
        Assert.True(histogram.Percentile(0.5d) <= LatencyHistogram.MinimumBoundMilliseconds);
    }

    [Fact]
    public void Histogram_Clear_StartsOver()
    {
        LatencyHistogram histogram = new();
        histogram.Record(10d);
        histogram.Clear();
        histogram.Record(2d);

        Assert.Equal(1, histogram.Count);
        Assert.Equal(2d, histogram.Percentile(0.99d));
        Assert.Equal(2d, histogram.MaxMilliseconds);
    }

    [Fact]
    public void Metrics_RecordAdvance_TracksPhasesTicksAndWindow()
    {
        ServerTickMetrics metrics = new();
        long oneMs = Stopwatch.Frequency / 1000;

        metrics.RecordAdvance(0, oneMs, 0, 0, capturedCheckpoint: false);
        metrics.RecordAdvance(1, 2 * oneMs, oneMs, oneMs, capturedCheckpoint: false);
        metrics.RecordAdvance(3, 4 * oneMs, 0, 0, capturedCheckpoint: true);
        metrics.RecordDroppedTicks(5);
        metrics.RecordWriteQueueDepth(7);
        metrics.RecordWriteQueueDepth(2);

        ServerTickMetricsSnapshot first = metrics.Capture(resetWindow: true);
        Assert.Equal(3, first.Version);
        Assert.Equal(2, first.TickedAdvances);
        Assert.Equal(4, first.SimulatedTicks);
        Assert.Equal(5, first.DroppedTicks);
        Assert.Equal(2, first.Tick.Count);
        Assert.Equal(2, first.TickWindow.Count);
        Assert.Equal(4d, first.Tick.MaxMilliseconds, 3);
        Assert.Equal(4d, first.LastAdvanceMilliseconds, 3);
        Assert.Equal(1d, first.MaxSnapshotMilliseconds, 3);
        Assert.Equal(1, first.CheckpointCaptureTicks);
        Assert.Equal(4d, first.MaxCheckpointCaptureTickMilliseconds, 3);
        Assert.Equal(2, first.WriteQueueDepth);
        Assert.Equal(7, first.MaxWriteQueueDepth);

        metrics.RecordAdvance(1, oneMs, 0, 0, capturedCheckpoint: false);
        ServerTickMetricsSnapshot second = metrics.Capture();
        Assert.Equal(3, second.Tick.Count);
        Assert.Equal(1, second.TickWindow.Count);
        Assert.Equal(1d, second.TickWindow.MaxMilliseconds, 3);
    }

    [Fact]
    public void Metrics_Recording_AllocatesNothing()
    {
        ServerTickMetrics metrics = new();
        long tick = 0;

        long bytes = AllocationAssert.AtMost(
            0,
            () =>
            {
                tick++;
                long start = ServerTickMetrics.Timestamp();
                long elapsed = ServerTickMetrics.Timestamp() - start + tick % 997;
                metrics.RecordAdvance(1, elapsed, elapsed / 2, elapsed / 3, capturedCheckpoint: tick % 320 == 0);
                metrics.RecordDroppedTicks(tick / 1000);
                metrics.RecordWriteQueueDepth((int)(tick % 5));
            },
            iterations: 1000);
        long captureBytes = AllocationAssert.AtMost(0, () => metrics.Capture(resetWindow: true));

        _output.WriteLine($"ServerTickMetrics record: {bytes} B, capture: {captureBytes} B");
    }

    [Fact]
    public async Task Service_CountsDroppedTicksAndExposesMetrics()
    {
        DedicatedServerHost host = new(new IdleAuthority(), CreateRouter());
        await using DedicatedServerService service = new(host, new IdleServerTransport(), new TransportJsonCodec(), new LengthPrefixedMessageFramer());

        // One second in one advance: the clock runs DefaultMaxTicksPerAdvance and drops the rest.
        int executed = await service.AdvanceOnceAsync(1d);
        await service.AdvanceOnceAsync(SimulationClock.FixedDeltaSeconds);

        ServerMetricsSnapshot metrics = service.CaptureMetrics();
        Assert.Equal(SimulationClock.DefaultMaxTicksPerAdvance, executed);
        Assert.Equal(SimulationClock.AuthoritativeTicksPerSecond - SimulationClock.DefaultMaxTicksPerAdvance, metrics.Tick.DroppedTicks);
        Assert.Equal(2, metrics.Tick.TickedAdvances);
        Assert.Equal(SimulationClock.DefaultMaxTicksPerAdvance + 1, metrics.Tick.SimulatedTicks);
        Assert.Equal(2, metrics.Tick.Tick.Count);
        Assert.Equal(0, metrics.Connections);
        Assert.True(service.LatestPerformance.HasCounters);
        Assert.Same(service.LatestPerformance, service.LatestPerformance);
    }

    [Fact]
    public void CheckpointWriter_RecordsWriteTimesAndEnqueues()
    {
        using BackgroundCheckpointWriter writer = new(new SlowCheckpointWriter(TimeSpan.FromMilliseconds(20)));

        writer.Enqueue(PersistenceCheckpoint.Empty);
        Assert.True(writer.Flush(TimeSpan.FromSeconds(10)));

        Assert.Equal(1, writer.EnqueuedCheckpoints);
        Assert.Equal(1, writer.CompletedWrites);
        Assert.True(writer.LastWriteMilliseconds >= 15d, $"last write {writer.LastWriteMilliseconds} ms");
        Assert.True(writer.MaxWriteMilliseconds >= writer.LastWriteMilliseconds);
        Assert.True(writer.MeanWriteMilliseconds >= 15d);
    }

    [Fact]
    public void PersistenceWriteQueue_PendingCount_FollowsTheBacklog()
    {
        using PersistenceWriteQueue queue = PersistenceWriteQueue.Background();
        using ManualResetEventSlim release = new();
        queue.Enqueue(() => release.Wait(TimeSpan.FromSeconds(10)));
        queue.Enqueue(() => { });
        queue.Enqueue(() => { });

        // The first write is running (dequeued) or about to be; the other two are still queued.
        Assert.InRange(queue.PendingCount, 2, 3);
        release.Set();
        Assert.True(queue.Flush(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, queue.PendingCount);
        Assert.Equal(0, PersistenceWriteQueue.Inline().PendingCount);
    }

    private static void AssertWithinBucket(double expected, double actual)
    {
        Assert.InRange(actual, expected, expected * LatencyHistogram.BucketGrowth + 1e-9d);
    }

    private static ProtocolRouter CreateRouter()
    {
        return new ProtocolRouter(
            [
                new ClientHelloHandler(),
                new JoinRequestHandler(),
                new PlayerCommandHandler(),
                new PlayerActionHandler()
            ],
            Array.Empty<IClientMessageHandler>());
    }

    private sealed class SlowCheckpointWriter : IPersistenceCheckpointWriter
    {
        private readonly TimeSpan _delay;

        public SlowCheckpointWriter(TimeSpan delay)
        {
            _delay = delay;
        }

        public void Write(PersistenceCheckpoint checkpoint)
        {
            Thread.Sleep(_delay);
        }
    }

    private sealed class IdleAuthority : ITickDrivenAuthoritativeSessionHost
    {
        private readonly ServerSnapshotEnvelope _snapshot = new(
            0,
            new Dictionary<string, ulong>(),
            Array.Empty<ActorSnapshot>(),
            PlayerInteractionMode.Building,
            null,
            Array.Empty<WorldMutationDelta>(),
            Array.Empty<DamageEventDelta>());

        public JoinAcceptedMessage JoinSession(string requestedSessionId, string displayName)
        {
            return new JoinAcceptedMessage(requestedSessionId, 101, 0, InitialWorld: null);
        }

        public AuthoritativeActionResult ProcessAction(PlayerActionMessage message)
        {
            throw new NotSupportedException();
        }

        public ServerSnapshotEnvelope ExportSnapshot(bool includeWorldSnapshot = false)
        {
            return _snapshot;
        }

        public IReadOnlyList<AuthoritativeActionResult> AdvanceTick(
            ulong serverTick,
            float deltaTime,
            IReadOnlyList<PlayerCommandFrame> commandFrames,
            IReadOnlyList<QueuedPlayerAction> queuedActions,
            PlayerSimulationTuning playerTuning,
            bool allowGameplaySimulation = true)
        {
            return Array.Empty<AuthoritativeActionResult>();
        }
    }

    private sealed class IdleServerTransport : IServerTransport
    {
        public int Port => 0;

        public void Start()
        {
        }

        public ValueTask<ITransportConnection> AcceptClientAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
