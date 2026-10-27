using OpenTK.Mathematics;

namespace Dream.Client.Presentation;

// Samples are keyed by authoritative server tick, so several snapshots drained in one
// frame and ticks skipped by outbound coalescing still interpolate over real tick spans.
public sealed class RemotePlayerInterpolationBuffer
{
    public const int DefaultMaxSamples = 32;
    public const ulong DefaultMaxTickSpan = 64;

    private readonly List<RemotePlayerSnapshotSample> _samples = [];
    private readonly int _maxSamples;
    private readonly ulong _maxTickSpan;

    public RemotePlayerInterpolationBuffer(int maxSamples = DefaultMaxSamples, ulong maxTickSpan = DefaultMaxTickSpan)
    {
        _maxSamples = Math.Max(2, maxSamples);
        _maxTickSpan = Math.Max(1UL, maxTickSpan);
    }

    public int Count => _samples.Count;

    public ulong? OldestTick => _samples.Count == 0 ? null : _samples[0].ServerTick;

    public ulong? NewestTick => _samples.Count == 0 ? null : _samples[^1].ServerTick;

    public void AddSample(RemotePlayerSnapshotSample sample)
    {
        if (_samples.Count > 0 &&
            _samples[^1].ServerTick > sample.ServerTick &&
            _samples[^1].ServerTick - sample.ServerTick > _maxTickSpan)
        {
            return;
        }

        int index = _samples.Count;
        while (index > 0 && _samples[index - 1].ServerTick > sample.ServerTick)
        {
            index--;
        }

        if (index > 0 && _samples[index - 1].ServerTick == sample.ServerTick)
        {
            _samples[index - 1] = sample;
        }
        else
        {
            _samples.Insert(index, sample);
        }

        ulong newestTick = _samples[^1].ServerTick;
        int pruneCount = 0;
        while (pruneCount < _samples.Count - 1 && newestTick - _samples[pruneCount].ServerTick > _maxTickSpan)
        {
            pruneCount++;
        }

        pruneCount = Math.Max(pruneCount, _samples.Count - _maxSamples);
        if (pruneCount > 0)
        {
            _samples.RemoveRange(0, pruneCount);
        }
    }

    public bool TrySample(double renderTick, out InterpolatedActorPose pose)
    {
        if (_samples.Count == 0)
        {
            pose = default;
            return false;
        }

        RemotePlayerSnapshotSample first = _samples[0];
        if (_samples.Count == 1 || renderTick <= first.ServerTick)
        {
            pose = new InterpolatedActorPose(first.Position, first.Facing);
            return true;
        }

        for (int index = 1; index < _samples.Count; index++)
        {
            RemotePlayerSnapshotSample previous = _samples[index - 1];
            RemotePlayerSnapshotSample current = _samples[index];
            if (renderTick > current.ServerTick)
            {
                continue;
            }

            double span = current.ServerTick - previous.ServerTick;
            float alpha = Math.Clamp((float)((renderTick - previous.ServerTick) / span), 0f, 1f);
            Vector3 facing = Vector3.Lerp(previous.Facing, current.Facing, alpha);
            pose = new InterpolatedActorPose(
                Vector3.Lerp(previous.Position, current.Position, alpha),
                facing.LengthSquared > 1e-8f ? facing.Normalized() : current.Facing);
            return true;
        }

        RemotePlayerSnapshotSample latest = _samples[^1];
        pose = new InterpolatedActorPose(latest.Position, latest.Facing);
        return true;
    }
}
