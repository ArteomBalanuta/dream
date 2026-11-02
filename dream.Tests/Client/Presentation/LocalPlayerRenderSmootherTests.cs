using Dream.Client.Presentation;
using Dream.Simulation.Runtime;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Client.Presentation;

public sealed class LocalPlayerRenderSmootherTests
{
    private const float Speed = 5.5f;
    private const float TickSeconds = SimulationClock.FixedDeltaSeconds;
    private readonly ITestOutputHelper _output;

    public LocalPlayerRenderSmootherTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // Walks at a constant speed on 64 Hz ticks and returns the relative std-dev of the rendered
    // per-frame speed (step / frame time) for the raw stepped body and for the smoother, plus the
    // largest model-versus-velocity-led-camera offset the old path produced (the new path draws
    // the model and the camera from the same position, so its offset is zero by construction).
    private static (double RawRelStd, double SmoothRelStd, double LegacyMaxOffset) Simulate(double frameHz, int seed)
    {
        var random = new Random(seed);
        var smoother = new LocalPlayerRenderSmoother();
        double time = 0;
        double nextTick = TickSeconds;
        double lastTickTime = 0;
        Vector3 body = Vector3.Zero;
        float rawPrev = 0f;
        float smoothPrev = 0f;
        var rawSpeeds = new List<double>();
        var smoothSpeeds = new List<double>();
        double legacyMax = 0;
        for (int frame = 0; frame < (int)(frameHz * 6); frame++)
        {
            double dt = (1.0 / frameHz) * (1.0 + (((random.NextDouble() * 2.0) - 1.0) * 0.3));
            time += dt;
            while (time >= nextTick)
            {
                Vector3 before = body;
                body += new Vector3(Speed * TickSeconds, 0f, 0f);
                smoother.RecordTick(before, body, TickSeconds);
                lastTickTime = nextTick;
                nextTick += TickSeconds;
            }

            float remaining = (float)(time - lastTickTime);
            LocalPlayerRenderPose pose = smoother.Resolve(body, remaining / TickSeconds, Vector3.UnitZ, (float)dt);
            if (time > 0.5)
            {
                legacyMax = Math.Max(legacyMax, Speed * remaining);
                rawSpeeds.Add((body.X - rawPrev) / dt);
                smoothSpeeds.Add((pose.Position.X - smoothPrev) / dt);
            }

            rawPrev = body.X;
            smoothPrev = pose.Position.X;
        }

        return (RelStd(rawSpeeds), RelStd(smoothSpeeds), legacyMax);
    }

    private static double RelStd(List<double> values)
    {
        double mean = values.Average();
        double variance = values.Sum(v => (v - mean) * (v - mean)) / values.Count;
        return Math.Sqrt(variance) / mean;
    }

    [Theory]
    [InlineData(60.0)]
    [InlineData(144.0)]
    [InlineData(240.0)]
    public void ConstantSpeedWalk_FrameSpeedIsFarSteadierThanRawBody(double frameHz)
    {
        var (raw, smooth, legacyMax) = Simulate(frameHz, 7);
        _output.WriteLine($"{frameHz} Hz: raw body speed relStd={raw:F3}, smoothed relStd={smooth:F3}, " +
            $"old model-vs-camera offset max={legacyMax * 100:F1} cm");
        Assert.True(smooth < raw * 0.5, $"smooth {smooth} raw {raw}");
        Assert.True(smooth < 0.2, $"smooth {smooth}");
    }

    [Fact]
    public void ReconcilerNudge_AppearsImmediately()
    {
        var smoother = new LocalPlayerRenderSmoother();
        smoother.RecordTick(Vector3.Zero, new Vector3(0.1f, 0f, 0f), TickSeconds);
        LocalPlayerRenderPose pose = smoother.Resolve(new Vector3(0.1f, 0f, 0.5f), 0.5f, Vector3.UnitZ, 0.016f);
        Assert.Equal(0.5f, pose.Position.Z, 4);
    }

    [Fact]
    public void LargeJump_SnapsWithoutInterpolating()
    {
        var smoother = new LocalPlayerRenderSmoother();
        smoother.RecordTick(Vector3.Zero, new Vector3(0.1f, 0f, 0f), TickSeconds);
        LocalPlayerRenderPose pose = smoother.Resolve(new Vector3(50f, 0f, 0f), 0.5f, Vector3.UnitZ, 0.016f);
        Assert.Equal(50f, pose.Position.X, 3);
    }

    [Fact]
    public void Facing_TurnsThroughShortestArc()
    {
        var smoother = new LocalPlayerRenderSmoother();
        Vector3 a = new(MathF.Sin(3.0f), 0f, MathF.Cos(3.0f));
        Vector3 b = new(MathF.Sin(-3.0f), 0f, MathF.Cos(-3.0f));
        smoother.Resolve(Vector3.Zero, 0f, a, 0.016f);
        LocalPlayerRenderPose pose = smoother.Resolve(Vector3.Zero, 0f, b, 0.016f);
        // The shortest arc from 3.0 to -3.0 rad crosses pi, so the facing stays on the -Z side.
        Assert.True(pose.Facing.Z < -0.9f, $"facing {pose.Facing}");
    }

    [Fact]
    public void Moving_HasHysteresis()
    {
        var smoother = new LocalPlayerRenderSmoother();
        Vector3 start = Vector3.Zero;
        Vector3 at = new(Speed * TickSeconds, 0f, 0f);
        smoother.RecordTick(start, start, TickSeconds);
        smoother.RecordTick(start, at, TickSeconds);
        Assert.True(smoother.Resolve(at, 0f, Vector3.UnitZ, 0.016f).IsMoving);
        smoother.RecordTick(at, at + new Vector3(0.2f * TickSeconds, 0f, 0f), TickSeconds);
        Assert.True(smoother.Resolve(at, 0f, Vector3.UnitZ, 0.016f).IsMoving);
        smoother.RecordTick(at, at, TickSeconds);
        Assert.False(smoother.Resolve(at, 0f, Vector3.UnitZ, 0.016f).IsMoving);
    }
}
