using System.Diagnostics;

namespace Dream.Server;

// Fixed-bucket latency histogram. Bucket bounds grow geometrically by 5% from 10 µs to 10 s, so
// a percentile read from it is within 5% of the true value (and never above the recorded max).
// Recording is a binary search plus a counter increment: no allocation, no locking. Callers that
// read from another thread synchronise externally (ServerTickMetrics holds a lock).
public sealed class LatencyHistogram
{
    public const double MinimumBoundMilliseconds = 0.01d;
    public const double MaximumBoundMilliseconds = 10_000d;
    public const double BucketGrowth = 1.05d;

    // Upper bound (inclusive) of each bucket, ascending. A value above the last bound goes to an
    // overflow bucket whose percentile reads back as the recorded maximum.
    private static readonly double[] UpperBounds = CreateUpperBounds();

    private readonly long[] _counts = new long[UpperBounds.Length + 1];

    public static int BucketCount => UpperBounds.Length + 1;

    public long Count { get; private set; }

    public double SumMilliseconds { get; private set; }

    public double MinMilliseconds { get; private set; }

    public double MaxMilliseconds { get; private set; }

    public double MeanMilliseconds => Count == 0 ? 0d : SumMilliseconds / Count;

    public void Record(double milliseconds)
    {
        if (double.IsNaN(milliseconds) || milliseconds < 0d)
        {
            milliseconds = 0d;
        }

        _counts[FindBucket(milliseconds)]++;
        if (Count == 0)
        {
            MinMilliseconds = milliseconds;
            MaxMilliseconds = milliseconds;
        }
        else
        {
            MinMilliseconds = Math.Min(MinMilliseconds, milliseconds);
            MaxMilliseconds = Math.Max(MaxMilliseconds, milliseconds);
        }

        Count++;
        SumMilliseconds += milliseconds;
    }

    // The smallest bucket bound below which at least `fraction` of the samples lie (nearest-rank),
    // clamped to the recorded min and max. 0 when empty.
    public double Percentile(double fraction)
    {
        if (Count == 0)
        {
            return 0d;
        }

        fraction = Math.Clamp(fraction, 0d, 1d);
        long rank = Math.Max(1L, (long)Math.Ceiling(fraction * Count));
        long cumulative = 0;
        for (int i = 0; i < _counts.Length; i++)
        {
            cumulative += _counts[i];
            if (cumulative >= rank)
            {
                double bound = i < UpperBounds.Length ? UpperBounds[i] : MaxMilliseconds;
                return Math.Clamp(bound, MinMilliseconds, MaxMilliseconds);
            }
        }

        return MaxMilliseconds;
    }

    public LatencySummary Summarize()
    {
        return new LatencySummary(
            Count,
            MeanMilliseconds,
            Percentile(0.50d),
            Percentile(0.95d),
            Percentile(0.99d),
            MaxMilliseconds);
    }

    public void Clear()
    {
        Array.Clear(_counts);
        Count = 0;
        SumMilliseconds = 0d;
        MinMilliseconds = 0d;
        MaxMilliseconds = 0d;
    }

    private static int FindBucket(double milliseconds)
    {
        int low = 0;
        int high = UpperBounds.Length - 1;
        if (milliseconds > UpperBounds[high])
        {
            return UpperBounds.Length;
        }

        while (low < high)
        {
            int mid = (low + high) >>> 1;
            if (milliseconds <= UpperBounds[mid])
            {
                high = mid;
            }
            else
            {
                low = mid + 1;
            }
        }

        return low;
    }

    private static double[] CreateUpperBounds()
    {
        List<double> bounds = [];
        for (double bound = MinimumBoundMilliseconds; bound < MaximumBoundMilliseconds; bound *= BucketGrowth)
        {
            bounds.Add(bound);
        }

        bounds.Add(MaximumBoundMilliseconds);
        return bounds.ToArray();
    }
}

public readonly record struct LatencySummary(
    long Count,
    double MeanMilliseconds,
    double P50Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds,
    double MaxMilliseconds)
{
    public static LatencySummary Empty => default;
}

// What the tick thread recorded, copied out under the metrics lock.
public readonly record struct ServerTickMetricsSnapshot(
    // Incremented by every AdvanceOnceAsync call, ticks or not.
    long Version,
    // AdvanceOnceAsync calls that executed at least one simulation tick.
    long TickedAdvances,
    long SimulatedTicks,
    // Ticks the server clock skipped after a stall (SimulationClock.DefaultMaxTicksPerAdvance).
    long DroppedTicks,
    // Advance + snapshot + broadcast of each ticked advance, since start and since the last reset.
    LatencySummary Tick,
    LatencySummary TickWindow,
    // Advance time divided by the ticks it ran: the simulation cost of one tick, whether or not the
    // loop had to run several back to back.
    LatencySummary AdvancePerTick,
    double LastAdvanceMilliseconds,
    double LastSnapshotMilliseconds,
    double LastBroadcastMilliseconds,
    double MaxAdvanceMilliseconds,
    double MaxSnapshotMilliseconds,
    double MaxBroadcastMilliseconds,
    // Ticked advances in which the host captured a persistence checkpoint, and their advance time
    // (the capture runs inside the tick, so this is capture plus that tick's simulation).
    long CheckpointCaptureTicks,
    double LastCheckpointCaptureTickMilliseconds,
    double MaxCheckpointCaptureTickMilliseconds,
    int WriteQueueDepth,
    int MaxWriteQueueDepth);

// Everything DedicatedServerService exposes for load tests and operators: the tick thread's
// figures plus persistence and per-connection counters read when the snapshot is taken.
public readonly record struct ServerMetricsSnapshot(
    ServerTickMetricsSnapshot Tick,
    int Connections,
    int JoinedConnections,
    long BytesSent,
    long FramesSent,
    long BytesReceived,
    long FramesReceived,
    long CoalescedSnapshots,
    long CheckpointWritesCompleted,
    long CheckpointWritesFailed,
    long CheckpointsSuperseded,
    double LastCheckpointWriteMilliseconds,
    double MaxCheckpointWriteMilliseconds,
    double MeanCheckpointWriteMilliseconds,
    long PersistenceWritesCompleted,
    long PersistenceWritesFailed,
    // Connections the server closed itself: no inbound frame within the idle timeout, a socket
    // write stalled past the send-stall timeout, or more unmerged messages queued than
    // ServerConnection.MaxPendingOutboundMessages.
    long IdleTimeoutCloses = 0,
    long SendStallCloses = 0,
    long BacklogOverflowCloses = 0,
    // Lagging connections whose merged snapshot deltas passed the cap and were replaced by a
    // server-requested full world resync.
    long CoalesceOverflowResyncs = 0);

// Always-on, allocation-free server tick metrics. The tick thread records phase timestamps from
// Stopwatch.GetTimestamp; readers on other threads copy a ServerTickMetricsSnapshot out under the
// same lock (uncontended on the tick thread except while a reader copies, which is microseconds).
public sealed class ServerTickMetrics
{
    private readonly object _sync = new();
    private readonly LatencyHistogram _tick = new();
    private readonly LatencyHistogram _tickWindow = new();
    private readonly LatencyHistogram _advancePerTick = new();
    private long _version;
    private long _tickedAdvances;
    private long _simulatedTicks;
    private long _droppedTicks;
    private double _lastAdvanceMs;
    private double _lastSnapshotMs;
    private double _lastBroadcastMs;
    private double _maxAdvanceMs;
    private double _maxSnapshotMs;
    private double _maxBroadcastMs;
    private long _checkpointCaptureTicks;
    private double _lastCheckpointCaptureTickMs;
    private double _maxCheckpointCaptureTickMs;
    private int _writeQueueDepth;
    private int _maxWriteQueueDepth;

    public static long Timestamp() => Stopwatch.GetTimestamp();

    public static double ToMilliseconds(long stopwatchTicks) =>
        stopwatchTicks <= 0 ? 0d : stopwatchTicks * 1000d / Stopwatch.Frequency;

    public long Version => Volatile.Read(ref _version);

    // Server-side connection closes and overflow resyncs. Monotonic, Interlocked, outside _sync:
    // they are recorded by whichever thread drops the connection.
    private long _idleTimeoutCloses;
    private long _sendStallCloses;
    private long _backlogOverflowCloses;
    private long _coalesceOverflowResyncs;

    public long IdleTimeoutCloses => Interlocked.Read(ref _idleTimeoutCloses);

    public long SendStallCloses => Interlocked.Read(ref _sendStallCloses);

    public long BacklogOverflowCloses => Interlocked.Read(ref _backlogOverflowCloses);

    public long CoalesceOverflowResyncs => Interlocked.Read(ref _coalesceOverflowResyncs);

    // Counts a connection the server closed for one of the liveness or backlog reasons; other
    // reasons are not counted here.
    public void RecordConnectionClosed(ServerConnectionCloseReason reason)
    {
        switch (reason)
        {
            case ServerConnectionCloseReason.IdleTimeout:
                Interlocked.Increment(ref _idleTimeoutCloses);
                break;
            case ServerConnectionCloseReason.SendStall:
                Interlocked.Increment(ref _sendStallCloses);
                break;
            case ServerConnectionCloseReason.BacklogOverflow:
                Interlocked.Increment(ref _backlogOverflowCloses);
                break;
        }
    }

    public void RecordCoalesceOverflowResync()
    {
        Interlocked.Increment(ref _coalesceOverflowResyncs);
    }

    // One AdvanceOnceAsync call. The phase durations are Stopwatch ticks; snapshot and broadcast
    // are 0 when nobody was joined. Only calls that executed a tick feed the histogram.
    public void RecordAdvance(
        int executedTicks,
        long advanceTicks,
        long snapshotTicks,
        long broadcastTicks,
        bool capturedCheckpoint)
    {
        lock (_sync)
        {
            RecordAdvanceLocked(executedTicks, advanceTicks, snapshotTicks, broadcastTicks, capturedCheckpoint);
        }
    }

    // RecordAdvance plus the dropped-tick total and the persistence write-queue depth, under one
    // lock acquisition. Pass a negative writeQueueDepth when there is no write queue.
    public void RecordAdvance(
        int executedTicks,
        long advanceTicks,
        long snapshotTicks,
        long broadcastTicks,
        bool capturedCheckpoint,
        long totalDroppedTicks,
        int writeQueueDepth)
    {
        lock (_sync)
        {
            RecordAdvanceLocked(executedTicks, advanceTicks, snapshotTicks, broadcastTicks, capturedCheckpoint);
            _droppedTicks = totalDroppedTicks;
            if (writeQueueDepth >= 0)
            {
                RecordWriteQueueDepthLocked(writeQueueDepth);
            }
        }
    }

    private void RecordAdvanceLocked(
        int executedTicks,
        long advanceTicks,
        long snapshotTicks,
        long broadcastTicks,
        bool capturedCheckpoint)
    {
        double advanceMs = ToMilliseconds(advanceTicks);
        double snapshotMs = ToMilliseconds(snapshotTicks);
        double broadcastMs = ToMilliseconds(broadcastTicks);
        _lastAdvanceMs = advanceMs;
        _lastSnapshotMs = snapshotMs;
        _lastBroadcastMs = broadcastMs;
        if (executedTicks > 0)
        {
            double totalMs = advanceMs + snapshotMs + broadcastMs;
            _tick.Record(totalMs);
            _tickWindow.Record(totalMs);
            _advancePerTick.Record(advanceMs / executedTicks);
            _tickedAdvances++;
            _simulatedTicks += executedTicks;
            _maxAdvanceMs = Math.Max(_maxAdvanceMs, advanceMs);
            _maxSnapshotMs = Math.Max(_maxSnapshotMs, snapshotMs);
            _maxBroadcastMs = Math.Max(_maxBroadcastMs, broadcastMs);
        }

        if (capturedCheckpoint)
        {
            _checkpointCaptureTicks++;
            _lastCheckpointCaptureTickMs = advanceMs;
            _maxCheckpointCaptureTickMs = Math.Max(_maxCheckpointCaptureTickMs, advanceMs);
        }

        _version++;
    }

    private void RecordWriteQueueDepthLocked(int depth)
    {
        _writeQueueDepth = depth;
        _maxWriteQueueDepth = Math.Max(_maxWriteQueueDepth, depth);
    }

    public void GetLastPhases(out double advanceMs, out double snapshotMs, out double broadcastMs)
    {
        lock (_sync)
        {
            advanceMs = _lastAdvanceMs;
            snapshotMs = _lastSnapshotMs;
            broadcastMs = _lastBroadcastMs;
        }
    }

    // Starts the latency distribution and the peaks (max phase times, max checkpoint tick, max
    // queue depth) over, for example after a load test's warm-up. Counters stay monotonic.
    public void ResetDistribution()
    {
        lock (_sync)
        {
            _tick.Clear();
            _tickWindow.Clear();
            _advancePerTick.Clear();
            _maxAdvanceMs = 0d;
            _maxSnapshotMs = 0d;
            _maxBroadcastMs = 0d;
            _maxCheckpointCaptureTickMs = 0d;
            _maxWriteQueueDepth = _writeQueueDepth;
        }
    }

    // resetWindow clears the windowed histogram after copying it, so periodic samplers get
    // per-interval percentiles next to the since-start ones.
    public ServerTickMetricsSnapshot Capture(bool resetWindow = false)
    {
        lock (_sync)
        {
            ServerTickMetricsSnapshot snapshot = new(
                _version,
                _tickedAdvances,
                _simulatedTicks,
                _droppedTicks,
                _tick.Summarize(),
                _tickWindow.Summarize(),
                _advancePerTick.Summarize(),
                _lastAdvanceMs,
                _lastSnapshotMs,
                _lastBroadcastMs,
                _maxAdvanceMs,
                _maxSnapshotMs,
                _maxBroadcastMs,
                _checkpointCaptureTicks,
                _lastCheckpointCaptureTickMs,
                _maxCheckpointCaptureTickMs,
                _writeQueueDepth,
                _maxWriteQueueDepth);
            if (resetWindow)
            {
                _tickWindow.Clear();
            }

            return snapshot;
        }
    }
}
