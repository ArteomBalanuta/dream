using Dream.Simulation.Runtime;

namespace Dream.Client.Presentation;

// Estimates the server's current tick on the client. Remote players render near
// EstimatedTick - InterpolationDelayTicks, so they stay between two received snapshots.
//
// RenderTick follows that target without ever stepping back: a late snapshot pulls the estimate
// back and widens the delay at the same moment, which used to jump remote players backwards.
// Instead of moving its position, RenderTick slews its advance rate (between 0x and 2x real time)
// toward the target, so it holds or slows while the target is behind and catches up when it is
// ahead. Only Reset and snaps move it discontinuously.
public sealed class ServerTickClock
{
    public const double DefaultInterpolationDelayTicks = 6d;
    public const double MinInterpolationDelayTicks = 3d;
    public const double MaxInterpolationDelayTicks = 12d;
    public const double SnapThresholdTicks = 16d;

    private const double SlewFraction = 0.1d;
    // Jitter rises quickly and decays slowly so a late burst widens the delay before the buffer runs dry.
    private const double JitterAttack = 0.25d;
    private const double JitterRelease = 0.02d;
    private const double IntervalSmoothing = 0.1d;
    private const double JitterDelayScale = 2d;
    private const double DelaySafetyTicks = 1d;
    private const double InitialIntervalTicks = 2d;
    private const double InitialJitterTicks = 1.5d;
    // Fraction of the render error closed per elapsed tick of real time (time constant 4 ticks).
    private const double RenderRateGain = 0.25d;
    private const double MinRenderRate = 0d;
    private const double MaxRenderRate = 2d;

    private double _estimatedTick;
    private ulong _newestObservedTick;
    private bool _hasEstimate;
    private bool _hasObservation;
    private double _snapshotIntervalTicks = InitialIntervalTicks;
    private double _jitterTicks = InitialJitterTicks;
    private double _renderTick;

    public bool HasEstimate => _hasEstimate;

    public double EstimatedTick => _estimatedTick;

    public ulong NewestObservedTick => _newestObservedTick;

    public double JitterTicks => _jitterTicks;

    public double SnapshotIntervalTicks => _snapshotIntervalTicks;

    // Monotonic tick remote players are rendered at; see the class comment.
    public double RenderTick => _renderTick;

    private double RenderTargetTick => _estimatedTick - InterpolationDelayTicks;

    public double InterpolationDelayTicks => Math.Clamp(
        _snapshotIntervalTicks + (JitterDelayScale * _jitterTicks) + DelaySafetyTicks,
        MinInterpolationDelayTicks,
        MaxInterpolationDelayTicks);

    public void Reset(ulong tick)
    {
        _estimatedTick = tick;
        _newestObservedTick = tick;
        _hasEstimate = true;
        _hasObservation = true;
        _snapshotIntervalTicks = InitialIntervalTicks;
        _jitterTicks = InitialJitterTicks;
        _renderTick = RenderTargetTick;
    }

    public void Advance(double elapsedSeconds)
    {
        if (!_hasEstimate || !(elapsedSeconds > 0d) || double.IsInfinity(elapsedSeconds))
        {
            return;
        }

        double elapsedTicks = elapsedSeconds * SimulationClock.AuthoritativeTicksPerSecond;
        double error = RenderTargetTick - _renderTick;
        _estimatedTick += elapsedTicks;

        double target = RenderTargetTick;
        if (error + elapsedTicks > SnapThresholdTicks)
        {
            // Far behind (for example after a long stall): catching up gradually would only
            // fast-forward through stale motion, so jump forward.
            _renderTick = target;
            return;
        }

        double rate = Math.Clamp(1d + (error * RenderRateGain), MinRenderRate, MaxRenderRate);
        double next = _renderTick + (elapsedTicks * rate);
        // Stop at the target instead of crossing it on a long frame, and never move backwards.
        _renderTick = Math.Min(next, Math.Max(_renderTick, target));
    }

    public void Observe(ulong snapshotTick)
    {
        if (!_hasEstimate)
        {
            Snap(snapshotTick);
            return;
        }

        if (_hasObservation && snapshotTick <= _newestObservedTick)
        {
            // Older or duplicate ticks carry no new timing information, unless the server
            // timeline restarted far behind what this clock has already seen.
            if (_newestObservedTick - snapshotTick > SnapThresholdTicks)
            {
                Snap(snapshotTick);
            }

            return;
        }

        double offset = snapshotTick - _estimatedTick;
        if (Math.Abs(offset) > SnapThresholdTicks)
        {
            Snap(snapshotTick);
            return;
        }

        if (_hasObservation)
        {
            double interval = snapshotTick - _newestObservedTick;
            _snapshotIntervalTicks += (Math.Min(interval, SnapThresholdTicks) - _snapshotIntervalTicks) * IntervalSmoothing;
        }

        double deviation = Math.Abs(offset);
        _jitterTicks += (deviation - _jitterTicks) * (deviation > _jitterTicks ? JitterAttack : JitterRelease);
        _estimatedTick += offset * SlewFraction;
        _newestObservedTick = snapshotTick;
        _hasObservation = true;
    }

    private void Snap(ulong tick)
    {
        _estimatedTick = tick;
        _newestObservedTick = tick;
        _hasEstimate = true;
        _hasObservation = true;
        _renderTick = RenderTargetTick;
    }
}
