using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Tests.Utilities;

namespace Dream.Tests.Client.Scenes;

// PlayScene needs a GL context, so its Update and Render cannot run here. These tests measure
// the per-frame patterns MEM-7 replaced in them, old against new, with the same calls PlayScene
// makes: the capturing tick lambda, the single-session command drain and the render timers.
// Measured (bytes per frame, Debug): capturing lambda 96 -> 0, Drain([id]) per tick 400 -> 0,
// seven Stopwatch.StartNew 280 -> 0.
public class PlaySceneFramePathAllocationTests
{
    private static readonly PlayerSessionId Session = new("session-local");

    private readonly ITestOutputHelper _output;

    public PlaySceneFramePathAllocationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void TickAdvance_StaticLambdaWithState_AllocatesNothing()
    {
        ServerTickRunner runner = new();
        TickState state = new();
        double frameSeconds = SimulationClock.FixedDeltaSeconds;

        long capturing = AllocationAssert.MeasureBytesPerIteration(() =>
        {
            int frameValue = state.FrameValue;
            bool triggered = false;
            runner.Advance(frameSeconds, (tick, fixedDeltaSeconds) =>
            {
                state.Total += frameValue;
                triggered = true;
            });
            state.Triggered = triggered;
        });

        long stateful = AllocationAssert.AtMost(0, () =>
        {
            state.Triggered = false;
            runner.Advance(
                frameSeconds,
                state,
                static (tickState, tick, fixedDeltaSeconds) =>
                {
                    tickState.Total += tickState.FrameValue;
                    tickState.Triggered = true;
                });
        });

        _output.WriteLine($"Tick advance: capturing lambda {capturing} B, static lambda with state {stateful} B");
        Assert.True(state.Triggered);
    }

    [Fact]
    public void CommandDrain_TryDrain_AllocatesNothing()
    {
        LatestPlayerCommandInbox inbox = new();
        ulong sequence = 1;

        long drain = AllocationAssert.MeasureBytesPerIteration(() =>
        {
            inbox.Enqueue(CreateFrame(sequence++));
            IReadOnlyList<PlayerCommandFrame> pending = inbox.Drain([Session]);
            _ = pending.Count > 0 ? pending[^1] : (PlayerCommandFrame?)null;
        });

        long tryDrain = AllocationAssert.AtMost(0, () =>
        {
            inbox.Enqueue(CreateFrame(sequence++));
            _ = inbox.TryDrain(Session, out PlayerCommandFrame frame) ? frame : (PlayerCommandFrame?)null;
        });

        _output.WriteLine($"Command drain: Drain([id]) {drain} B, TryDrain {tryDrain} B");
    }

    [Fact]
    public void RenderTimers_GetTimestamp_AllocatesNothing()
    {
        double sink = 0d;

        long stopwatches = AllocationAssert.MeasureBytesPerIteration(() =>
        {
            for (int i = 0; i < 7; i++)
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                stopwatch.Stop();
                sink += stopwatch.Elapsed.TotalMilliseconds;
            }
        });

        long timestamps = AllocationAssert.AtMost(0, () =>
        {
            for (int i = 0; i < 7; i++)
            {
                long start = Stopwatch.GetTimestamp();
                sink += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
        });

        _output.WriteLine($"Render timers: 7 x Stopwatch.StartNew {stopwatches} B, 7 x GetTimestamp {timestamps} B (sink {sink >= 0d})");
    }

    private static PlayerCommandFrame CreateFrame(ulong sequence)
    {
        return new PlayerCommandFrame(
            Session,
            sequence,
            new PlayerModeSwitchIntent(null),
            new PlayerMoveIntent(1f, 0f, 0f, JumpPressed: false, FreezePressed: false, ToggleNoclipPressed: false),
            new PlayerLookIntent(0f, 0f, null),
            false);
    }

    private sealed class TickState
    {
        public int FrameValue { get; } = 3;

        public long Total { get; set; }

        public bool Triggered { get; set; }
    }
}
