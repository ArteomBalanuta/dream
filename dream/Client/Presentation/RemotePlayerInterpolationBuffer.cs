namespace Dream.Client.Presentation;

// A remote player's tick-keyed pose buffer. The interpolation itself lives in
// TickInterpolationBuffer, which remote enemies share.
public sealed class RemotePlayerInterpolationBuffer
{
    public const int DefaultMaxSamples = TickInterpolationBuffer.DefaultMaxSamples;
    public const ulong DefaultMaxTickSpan = TickInterpolationBuffer.DefaultMaxTickSpan;

    private readonly TickInterpolationBuffer _buffer;

    public RemotePlayerInterpolationBuffer(int maxSamples = DefaultMaxSamples, ulong maxTickSpan = DefaultMaxTickSpan)
    {
        _buffer = new TickInterpolationBuffer(maxSamples, maxTickSpan);
    }

    public int Count => _buffer.Count;

    public ulong? OldestTick => _buffer.OldestTick;

    public ulong? NewestTick => _buffer.NewestTick;

    public void AddSample(RemotePlayerSnapshotSample sample)
    {
        _buffer.AddSample(sample.ServerTick, sample.Position, sample.Facing);
    }

    public bool TrySample(double renderTick, out InterpolatedActorPose pose)
    {
        return _buffer.TrySample(renderTick, out pose);
    }
}
