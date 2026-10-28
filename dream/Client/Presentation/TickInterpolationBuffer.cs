using OpenTK.Mathematics;

namespace Dream.Client.Presentation;

public readonly record struct TickPoseSample(
    ulong ServerTick,
    Vector3 Position,
    Vector3 Facing);

// Pose samples keyed by authoritative server tick, shared by remote players and remote enemies.
// Several snapshots drained in one frame and ticks skipped by outbound coalescing still
// interpolate over real tick spans. The sample list grows to MaxSamples + 1 and is then reused,
// so steady-state inserts, prunes and samples allocate nothing.
public sealed class TickInterpolationBuffer
{
    public const int DefaultMaxSamples = 32;
    public const ulong DefaultMaxTickSpan = 64;

    private readonly List<TickPoseSample> _samples = [];
    private readonly int _maxSamples;
    private readonly ulong _maxTickSpan;

    public TickInterpolationBuffer(int maxSamples = DefaultMaxSamples, ulong maxTickSpan = DefaultMaxTickSpan)
    {
        _maxSamples = Math.Max(2, maxSamples);
        _maxTickSpan = Math.Max(1UL, maxTickSpan);
    }

    public int Count => _samples.Count;

    public ulong? OldestTick => _samples.Count == 0 ? null : _samples[0].ServerTick;

    public ulong? NewestTick => _samples.Count == 0 ? null : _samples[^1].ServerTick;

    public bool TryGetNewest(out TickPoseSample sample)
    {
        if (_samples.Count == 0)
        {
            sample = default;
            return false;
        }

        sample = _samples[^1];
        return true;
    }

    // Drops every sample but keeps the storage, so a teleport (respawn) starts a fresh timeline.
    public void Clear()
    {
        _samples.Clear();
    }

    public void AddSample(ulong serverTick, Vector3 position, Vector3 facing)
    {
        AddSample(new TickPoseSample(serverTick, position, facing));
    }

    public void AddSample(TickPoseSample sample)
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

        TickPoseSample first = _samples[0];
        if (_samples.Count == 1 || renderTick <= first.ServerTick)
        {
            pose = new InterpolatedActorPose(first.Position, first.Facing);
            return true;
        }

        for (int index = 1; index < _samples.Count; index++)
        {
            TickPoseSample previous = _samples[index - 1];
            TickPoseSample current = _samples[index];
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

        TickPoseSample latest = _samples[^1];
        pose = new InterpolatedActorPose(latest.Position, latest.Facing);
        return true;
    }
}
