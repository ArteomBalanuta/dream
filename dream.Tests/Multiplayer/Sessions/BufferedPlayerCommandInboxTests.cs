using Xunit;
using Xunit.Abstractions;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Tests.Utilities;

namespace Dream.Tests.Multiplayer.Sessions;

public class BufferedPlayerCommandInboxTests(ITestOutputHelper output)
{
    private static readonly PlayerSessionId SessionA = new("session-a");
    private static readonly PlayerSessionId SessionB = new("session-b");

    [Fact]
    public void Pop_ReturnsOneFramePerTickInSequenceOrder()
    {
        BufferedPlayerCommandInbox inbox = new();
        for (ulong sequence = 1; sequence <= 3; sequence++)
        {
            inbox.Enqueue(Frame(SessionA, sequence, forward: sequence));
        }

        inbox.Enqueue(Frame(SessionB, 7));
        List<PlayerCommandFrame> drained = [];

        inbox.DrainInto([SessionA, SessionB], drained);
        Assert.Collection(
            drained,
            first => Assert.Equal((SessionA, 1UL), (first.SessionId, first.Sequence)),
            second => Assert.Equal((SessionB, 7UL), (second.SessionId, second.Sequence)));

        inbox.DrainInto([SessionA, SessionB], drained);
        Assert.Equal(2UL, Assert.Single(drained).Sequence);
        inbox.DrainInto([SessionA, SessionB], drained);
        Assert.Equal(3UL, Assert.Single(drained).Sequence);
        inbox.DrainInto([SessionA, SessionB], drained);
        Assert.Empty(drained);
        Assert.False(inbox.HasPending);
    }

    [Fact]
    public void StaleAndDuplicateFrames_AreDropped()
    {
        BufferedPlayerCommandInbox inbox = new();
        inbox.Enqueue(Frame(SessionA, 5));
        inbox.Enqueue(Frame(SessionA, 5, jump: true));
        inbox.Enqueue(Frame(SessionA, 3, jump: true));
        inbox.Enqueue(Frame(SessionA, 6));

        Assert.Equal(2, inbox.GetPendingTicks(SessionA));
        Assert.True(inbox.TryPop(SessionA, out PlayerCommandFrame first));
        Assert.Equal(5UL, first.Sequence);
        Assert.False(first.MoveIntent.JumpPressed);

        // A frame at or below the last accepted sequence stays stale after the ring drains.
        Assert.True(inbox.TryPop(SessionA, out _));
        inbox.Enqueue(Frame(SessionA, 6));
        inbox.Enqueue(Frame(SessionA, 4));
        Assert.False(inbox.TryPop(SessionA, out _));
    }

    [Fact]
    public void Overflow_MergesTheOldestTwo_KeepingPressesAndEveryTick()
    {
        BufferedPlayerCommandInbox inbox = new();
        inbox.Enqueue(Frame(SessionA, 1, forward: 0.25f, jump: true, yawDelta: 1f));
        inbox.Enqueue(Frame(SessionA, 2, forward: 0.5f, primaryUse: true, yawDelta: 2f));
        for (ulong sequence = 3; sequence <= 9; sequence++)
        {
            inbox.Enqueue(Frame(SessionA, sequence, forward: 1f));
        }

        // Nine frames, eight slots: no tick of input is lost.
        Assert.Equal(9, inbox.GetPendingTicks(SessionA));

        List<PlayerCommandFrame> popped = PopAll(inbox, SessionA);
        Assert.Equal(9, popped.Count);
        Assert.Equal(Enumerable.Range(1, 9).Select(value => (ulong)value), popped.Select(frame => frame.Sequence));

        // The merged slot (frames 1 and 2) keeps both presses, the newer axes, and its summed look
        // deltas on its first tick only.
        Assert.True(popped[0].MoveIntent.JumpPressed);
        Assert.True(popped[0].PrimaryUsePressed);
        Assert.Equal(0.5f, popped[0].MoveIntent.ForwardAxis);
        Assert.Equal(3f, popped[0].LookIntent.YawDelta);
        Assert.Equal(0f, popped[1].LookIntent.YawDelta);
        Assert.True(popped[1].MoveIntent.JumpPressed);
        Assert.False(popped[2].MoveIntent.JumpPressed);
        Assert.Equal(1f, popped[8].MoveIntent.ForwardAxis);
    }

    [Fact]
    public void OneSecondBurst_ReplaysEveryTickThroughEightSlots()
    {
        BufferedPlayerCommandInbox inbox = new();
        for (ulong sequence = 1; sequence <= 64; sequence++)
        {
            inbox.Enqueue(Frame(SessionA, sequence, forward: 1f, jump: sequence == 40));
        }

        Assert.Equal(64, inbox.GetPendingTicks(SessionA));
        List<PlayerCommandFrame> popped = PopAll(inbox, SessionA);
        Assert.Equal(64, popped.Count);
        Assert.Contains(popped, frame => frame.MoveIntent.JumpPressed);
        Assert.Equal(64UL, popped[^1].Sequence);
        for (int i = 1; i < popped.Count; i++)
        {
            Assert.True(popped[i].Sequence > popped[i - 1].Sequence);
        }
    }

    [Fact]
    public void BacklogAfterStarvation_SkipsTheTicksTheRuntimeAlreadyRepeated()
    {
        BufferedPlayerCommandInbox inbox = new();
        inbox.Enqueue(Frame(SessionA, 1, forward: 1f));
        Assert.True(inbox.TryPop(SessionA, out _));

        // 64 starved ticks: the runtime repeated frame 1 for 16 of them, then idled.
        for (int i = 0; i < 64; i++)
        {
            Assert.False(inbox.TryPop(SessionA, out _));
        }

        for (ulong sequence = 2; sequence <= 65; sequence++)
        {
            inbox.Enqueue(Frame(SessionA, sequence, forward: 1f, primaryUse: sequence == 3));
        }

        List<PlayerCommandFrame> popped = PopAll(inbox, SessionA);
        Assert.Equal(64 - WorldSessionRuntime.MaxCommandRepeatTicks, popped.Count);
        Assert.Equal(2UL + (ulong)WorldSessionRuntime.MaxCommandRepeatTicks, popped[0].Sequence);
        Assert.Equal(65UL, popped[^1].Sequence);
        // The press inside the skipped ticks carries into the first replayed one.
        Assert.True(popped[0].PrimaryUsePressed);
    }

    [Fact]
    public void BacklogThatDiffersFromTheRepeatedFrame_IsNotSkipped()
    {
        BufferedPlayerCommandInbox inbox = new();
        inbox.Enqueue(Frame(SessionA, 1, forward: 1f));
        Assert.True(inbox.TryPop(SessionA, out _));
        for (int i = 0; i < 20; i++)
        {
            Assert.False(inbox.TryPop(SessionA, out _));
        }

        for (ulong sequence = 2; sequence <= 11; sequence++)
        {
            inbox.Enqueue(Frame(SessionA, sequence, forward: -1f));
        }

        Assert.Equal(10, PopAll(inbox, SessionA).Count);
    }

    [Fact]
    public void ASenderAtTheTickRateWithJitter_IsReplayedFrameForFrame()
    {
        BufferedPlayerCommandInbox inbox = new();
        // Arrivals per tick: 1, 0, 2, 1, 1, 0, 0, 3, ... averaging one.
        int[] pattern = [1, 0, 2, 1, 1, 0, 0, 3];
        ulong sent = 0;
        ulong lastPopped = 0;
        int popped = 0;
        int repeated = 0;
        for (int tick = 0; tick < 512; tick++)
        {
            for (int i = 0; i < pattern[tick % pattern.Length]; i++)
            {
                sent++;
                inbox.Enqueue(Frame(SessionA, sent, forward: 1f));
            }

            if (inbox.TryPop(SessionA, out PlayerCommandFrame frame))
            {
                Assert.True(frame.Sequence > lastPopped);
                lastPopped = frame.Sequence;
                popped++;
            }
            else if (popped > 0)
            {
                // The runtime repeats the last frame on this tick.
                repeated++;
            }

            Assert.InRange(inbox.GetPendingTicks(SessionA), 0, 3);
        }

        // Every frame sent covers exactly one simulated tick: popped, or already repeated.
        output.WriteLine($"jittered 64 Hz sender: {sent} sent, {popped} popped, {repeated} repeated");
        Assert.InRange(popped + repeated, (int)sent - 3, (int)sent);
    }

    // With the facing changing every frame nothing can be skipped, so the ring grows until it
    // absorbs the jitter and then holds that depth: every frame is popped, none is lost.
    [Fact]
    public void ASenderAtTheTickRateWithJitterAndChangingFacing_SettlesAtTheJitterDepth()
    {
        BufferedPlayerCommandInbox inbox = new();
        int[] pattern = [1, 0, 2, 1, 1, 0, 0, 3];
        ulong sent = 0;
        int popped = 0;
        int maxDepth = 0;
        for (int tick = 0; tick < 512; tick++)
        {
            for (int i = 0; i < pattern[tick % pattern.Length]; i++)
            {
                sent++;
                float angle = sent * 0.01f;
                inbox.Enqueue(Frame(SessionA, sent, forward: 1f) with
                {
                    LookIntent = new PlayerLookIntent(0f, 0f, new OpenTK.Mathematics.Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle))),
                });
            }

            if (inbox.TryPop(SessionA, out _))
            {
                popped++;
            }

            maxDepth = Math.Max(maxDepth, inbox.GetPendingTicks(SessionA));
        }

        output.WriteLine($"jittered 64 Hz sender with changing facing: {sent} sent, {popped} popped, max depth {maxDepth}");
        Assert.InRange(maxDepth, 0, BufferedPlayerCommandInbox.ShedDepthThreshold);
        Assert.InRange(popped, (int)sent - BufferedPlayerCommandInbox.ShedDepthThreshold, (int)sent);
    }

    [Theory]
    [InlineData(144)]
    [InlineData(100)]
    [InlineData(240)]
    public void FastRenderRateSender_KeepsDepthAndLatencyBounded(int sendHz)
    {
        BufferedPlayerCommandInbox inbox = new();
        double sendAccumulator = 0;
        ulong sent = 0;
        int maxDepthAfterSettle = 0;
        ulong maxLagAfterSettle = 0;
        const int Ticks = 64 * 5;
        for (int tick = 0; tick < Ticks; tick++)
        {
            sendAccumulator += sendHz / (double)SimulationClock.AuthoritativeTicksPerSecond;
            while (sendAccumulator >= 1)
            {
                sendAccumulator -= 1;
                sent++;
                inbox.Enqueue(Frame(SessionA, sent, forward: 1f));
            }

            Assert.True(inbox.TryPop(SessionA, out PlayerCommandFrame frame));
            if (tick >= SimulationClock.AuthoritativeTicksPerSecond)
            {
                maxDepthAfterSettle = Math.Max(maxDepthAfterSettle, inbox.GetPendingTicks(SessionA));
                // Latency in ticks: frames sent this tick or earlier that the popped frame lags behind,
                // converted from send frames to ticks.
                ulong lagFrames = sent - frame.Sequence;
                maxLagAfterSettle = Math.Max(maxLagAfterSettle, lagFrames * (ulong)SimulationClock.AuthoritativeTicksPerSecond / (ulong)sendHz);
            }
        }

        output.WriteLine($"{sendHz} Hz sender: max depth {maxDepthAfterSettle} ticks, max lag {maxLagAfterSettle} ticks after the first second");
        Assert.InRange(maxDepthAfterSettle, 0, BufferedPlayerCommandInbox.ShedDepthThreshold);
        Assert.InRange(maxLagAfterSettle, 0UL, 3UL);
    }

    [Fact]
    public void SteadySenderOnTopOfABacklog_IsShedToCutLatency_KeepingPresses()
    {
        BufferedPlayerCommandInbox inbox = new();
        ulong sent = 0;
        for (int i = 0; i < 20; i++)
        {
            sent++;
            inbox.Enqueue(Frame(SessionA, sent, forward: 1f));
        }

        bool sawJump = false;
        int shedTick = -1;
        for (int tick = 0; tick < 128; tick++)
        {
            sent++;
            inbox.Enqueue(Frame(SessionA, sent, forward: 1f, jump: sent == 45));
            Assert.True(inbox.TryPop(SessionA, out PlayerCommandFrame frame));
            sawJump |= frame.MoveIntent.JumpPressed;
            if (shedTick < 0 && inbox.GetPendingTicks(SessionA) <= 1)
            {
                shedTick = tick;
            }
        }

        output.WriteLine($"backlog of 20 shed after {shedTick} ticks");
        Assert.InRange(shedTick, BufferedPlayerCommandInbox.ShedWindowTicks - 1, BufferedPlayerCommandInbox.ShedWindowTicks + 1);
        Assert.True(sawJump, "the press inside the shed backlog was lost");
        Assert.InRange(inbox.GetPendingTicks(SessionA), 0, 2);
    }

    [Fact]
    public void DrainingBacklog_IsNotShed()
    {
        BufferedPlayerCommandInbox inbox = new();
        for (ulong sequence = 1; sequence <= 60; sequence++)
        {
            inbox.Enqueue(Frame(SessionA, sequence, forward: 1f));
        }

        Assert.Equal(60, PopAll(inbox, SessionA).Count);
    }

    [Fact]
    public void Remove_FreesTheSession_SoAReconnectStartsAFreshSequence()
    {
        BufferedPlayerCommandInbox inbox = new();
        inbox.Enqueue(Frame(SessionA, 40));
        inbox.Enqueue(Frame(SessionA, 41));
        inbox.Enqueue(Frame(SessionB, 2));

        inbox.Remove(SessionA);

        Assert.False(inbox.Contains(SessionA));
        Assert.Equal(0, inbox.GetPendingTicks(SessionA));
        Assert.False(inbox.TryPop(SessionA, out _));
        Assert.True(inbox.Contains(SessionB));

        inbox.Enqueue(Frame(SessionA, 1));
        Assert.True(inbox.TryPop(SessionA, out PlayerCommandFrame frame));
        Assert.Equal(1UL, frame.Sequence);
    }

    [Fact]
    public void EnqueueAndPop_AllocateNothingAfterWarmUp()
    {
        BufferedPlayerCommandInbox inbox = new();
        List<PlayerSessionId> sessions = [SessionA, SessionB];
        List<PlayerCommandFrame> drained = [];
        ulong sequence = 0;

        long emptyBytes = AllocationAssert.AtMost(0, () => inbox.DrainInto(sessions, drained));
        long steadyBytes = AllocationAssert.AtMost(
            0,
            () =>
            {
                sequence++;
                inbox.Enqueue(Frame(SessionA, sequence, forward: 1f, yawDelta: 0.5f));
                inbox.Enqueue(Frame(SessionB, sequence, forward: 1f));
                inbox.DrainInto(sessions, drained);
            });
        long overflowBytes = AllocationAssert.AtMost(
            0,
            () =>
            {
                // Overflow merges, the shed and the fast-sender coalescing all run here.
                for (int i = 0; i < 10; i++)
                {
                    sequence++;
                    inbox.Enqueue(Frame(SessionA, sequence, forward: 1f, jump: i == 0));
                }

                inbox.DrainInto(sessions, drained);
            });
        inbox.Remove(SessionA);
        long reuseBytes = AllocationAssert.AtMost(
            0,
            () =>
            {
                inbox.Enqueue(Frame(SessionA, 1));
                inbox.DrainInto(sessions, drained);
                inbox.Remove(SessionA);
            });

        output.WriteLine($"BufferedPlayerCommandInbox: {emptyBytes} B empty, {steadyBytes} B steady, {overflowBytes} B overflow, {reuseBytes} B remove and rejoin");
    }

    private static List<PlayerCommandFrame> PopAll(BufferedPlayerCommandInbox inbox, PlayerSessionId session)
    {
        List<PlayerCommandFrame> popped = [];
        while (inbox.TryPop(session, out PlayerCommandFrame frame))
        {
            popped.Add(frame);
            Assert.True(popped.Count <= 1000);
        }

        return popped;
    }

    private static PlayerCommandFrame Frame(
        PlayerSessionId session,
        ulong sequence,
        float forward = 0f,
        bool jump = false,
        bool primaryUse = false,
        float yawDelta = 0f)
    {
        return new PlayerCommandFrame(
            session,
            sequence,
            PlayerModeSwitchIntent.None,
            new PlayerMoveIntent(forward, 0f, JumpPressed: jump),
            new PlayerLookIntent(yawDelta, 0f),
            primaryUse);
    }
}
