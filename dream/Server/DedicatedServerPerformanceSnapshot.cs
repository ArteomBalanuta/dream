namespace Dream.Server;

public sealed record DedicatedServerPerformanceSnapshot(
    double TickMilliseconds,
    double SnapshotMilliseconds,
    double BroadcastMilliseconds,
    bool Sampled = false)
{
    public static DedicatedServerPerformanceSnapshot Empty { get; } = new(0d, 0d, 0d);

    public bool HasCounters => Sampled;

    public IEnumerable<string> ToHudLines()
    {
        yield return $"Server: tick {TickMilliseconds:0.00} ms, snapshot {SnapshotMilliseconds:0.00} ms, broadcast {BroadcastMilliseconds:0.00} ms";
    }
}
