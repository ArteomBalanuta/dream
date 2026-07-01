using OpenTK.Mathematics;

namespace Dream.Client.Presentation;


public sealed class RemotePlayerInterpolationBuffer
{
    private readonly List<RemotePlayerSnapshotSample> _samples = [];
    private readonly int _maxSamples;

    public RemotePlayerInterpolationBuffer(int maxSamples = 8)
    {
        _maxSamples = Math.Max(2, maxSamples);
    }

    public void AddSample(RemotePlayerSnapshotSample sample)
    {
        _samples.Add(sample);
        _samples.Sort(static (left, right) => left.ReceivedAtSeconds.CompareTo(right.ReceivedAtSeconds));
        if (_samples.Count > _maxSamples)
        {
            _samples.RemoveRange(0, _samples.Count - _maxSamples);
        }
    }

    public bool TrySample(double renderTimeSeconds, out InterpolatedActorPose pose)
    {
        if (_samples.Count == 0)
        {
            pose = default;
            return false;
        }

        if (_samples.Count == 1 || renderTimeSeconds <= _samples[0].ReceivedAtSeconds)
        {
            RemotePlayerSnapshotSample first = _samples[0];
            pose = new InterpolatedActorPose(first.Position, first.Facing);
            return true;
        }

        for (int index = 1; index < _samples.Count; index++)
        {
            RemotePlayerSnapshotSample previous = _samples[index - 1];
            RemotePlayerSnapshotSample current = _samples[index];
            if (renderTimeSeconds > current.ReceivedAtSeconds)
            {
                continue;
            }

            double span = current.ReceivedAtSeconds - previous.ReceivedAtSeconds;
            float alpha = span <= 0d
                ? 1f
                : Math.Clamp((float)((renderTimeSeconds - previous.ReceivedAtSeconds) / span), 0f, 1f);
            pose = new InterpolatedActorPose(
                Vector3.Lerp(previous.Position, current.Position, alpha),
                Vector3.Lerp(previous.Facing, current.Facing, alpha).Normalized());
            return true;
        }

        RemotePlayerSnapshotSample latest = _samples[^1];
        pose = new InterpolatedActorPose(latest.Position, latest.Facing);
        return true;
    }
}
