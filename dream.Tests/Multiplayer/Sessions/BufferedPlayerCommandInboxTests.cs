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

    // No two neighbours move alike (frames 3-9 differ in strafe), so the oldest two merge.
    [Fact]
    public void Overflow_WithoutMatchingNeighbours_MergesTheOldestTwo_KeepingPressesAndEveryTick()
    {
        BufferedPlayerCommandInbox inbox = new();
        inbox.Enqueue(Frame(SessionA, 1, forward: 0.25f, jump: true, yawDelta: 1f));
        inbox.Enqueue(Frame(SessionA, 2, forward: 0.5f, primaryUse: true, yawDelta: 2f));
        for (ulong sequence = 3; sequence <= 9; sequence++)
        {
            inbox.Enqueue(Frame(SessionA, sequence, forward: 1f, strafe: sequence * 0.01f));
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

    // Facing is absolute, so a player turning while walking still has its repeated ticks matched
    // (comparing facing exactly used to disable skipping as soon as the player turned): every
    // frame sent covers exactly one simulated tick and the depth stays at the jitter depth.
    [Fact]
    public void ASenderAtTheTickRateWithJitterAndChangingFacing_StillSkipsTheRepeatedTicks()
    {
        BufferedPlayerCommandInbox inbox = new();
        int[] pattern = [1, 0, 2, 1, 1, 0, 0, 3];
        ulong sent = 0;
        ulong lastPopped = 0;
        int popped = 0;
        int repeated = 0;
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

            if (inbox.TryPop(SessionA, out PlayerCommandFrame frame))
            {
                Assert.True(frame.Sequence > lastPopped);
                lastPopped = frame.Sequence;
                popped++;
            }
            else if (popped > 0)
            {
                repeated++;
            }

            maxDepth = Math.Max(maxDepth, inbox.GetPendingTicks(SessionA));
        }

        output.WriteLine($"jittered 64 Hz sender with changing facing: {sent} sent, {popped} popped, {repeated} repeated, max depth {maxDepth}");
        Assert.InRange(maxDepth, 0, BufferedPlayerCommandInbox.ShedDepthThreshold);
        Assert.True(repeated > 0);
        Assert.InRange(popped + repeated, (int)sent - 3, (int)sent);
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

    // A backlog of runs keeps each tick's movement: the overflow merges the oldest neighbours that
    // move alike (5 and 6) instead of folding a walk tick into an idle one.
    [Fact]
    public void Overflow_MergesTheOldestNeighboursThatMoveAlike_SoEveryTickKeepsItsMovement()
    {
        BufferedPlayerCommandInbox inbox = new();
        float[] forward = [1f, 0f, 1f, 0f, 1f, 1f, 0f, 1f, 0f];
        for (int i = 0; i < forward.Length; i++)
        {
            inbox.Enqueue(Frame(SessionA, (ulong)(i + 1), forward: forward[i]));
        }

        List<PlayerCommandFrame> popped = PopAll(inbox, SessionA);

        Assert.Equal(Enumerable.Range(1, 9).Select(value => (ulong)value), popped.Select(frame => frame.Sequence));
        Assert.Equal(forward, popped.Select(frame => frame.MoveIntent.ForwardAxis));
    }

    // Reviewer S2: a frame the client folded from several ticks (its sequence jumps) stands for
    // every one of them, popped one per tick with sequences counted back from its own.
    [Fact]
    public void SequenceJump_CountsEveryTickTheFoldedFrameStandsFor()
    {
        BufferedPlayerCommandInbox inbox = new();
        inbox.Enqueue(Frame(SessionA, 1, forward: 1f));
        inbox.Enqueue(Frame(SessionA, 5, forward: 0.5f, jump: true));

        Assert.Equal(5, inbox.GetPendingTicks(SessionA));
        List<PlayerCommandFrame> popped = PopAll(inbox, SessionA);
        Assert.Equal([1UL, 2UL, 3UL, 4UL, 5UL], popped.Select(frame => frame.Sequence).ToArray());
        Assert.All(popped.Skip(1), frame => Assert.Equal(0.5f, frame.MoveIntent.ForwardAxis));
    }

    // Tester bug 2, the inbox half: a 1 s send stall. The client walked for the 16-tick repeat
    // window and then predicted and sent idle (ClientCommandTickStream); its outbound queue folded
    // the backlog into runs. The server repeated 16 ticks and idled 48, so all 64 starved ticks are
    // skipped and the first fresh tick is the first one after the stall: the server simulated
    // exactly the walk ticks the client predicted up to every ack.
    [Fact]
    public void SendStallBacklog_SkipsEveryStarvedTick_TheRepeatedOnesAndTheIdleOnes()
    {
        BufferedPlayerCommandInbox inbox = new();
        inbox.Enqueue(Frame(SessionA, 1, forward: 1f));
        Assert.True(inbox.TryPop(SessionA, out _));
        int serverWalkTicks = 1;
        for (int i = 0; i < 64; i++)
        {
            Assert.False(inbox.TryPop(SessionA, out _));
            serverWalkTicks += i < WorldSessionRuntime.MaxCommandRepeatTicks ? 1 : 0;
        }

        Assert.Equal(64, inbox.GetSkipDebtTicks(SessionA));

        Dream.Client.Networking.ClientOutboundCommandQueue outbound = new();
        int clientWalkTicks = 1;
        for (ulong sequence = 2; sequence <= 66; sequence++)
        {
            bool walk = sequence <= 1 + (ulong)WorldSessionRuntime.MaxCommandRepeatTicks || sequence == 66;
            clientWalkTicks += walk ? 1 : 0;
            outbound.TryEnqueue(Frame(SessionA, sequence, forward: walk ? 1f : 0f));
        }

        Assert.True(outbound.MergedCount > 0);
        while (outbound.TryDequeue(out PlayerCommandFrame sent))
        {
            inbox.Enqueue(sent);
        }

        Assert.True(inbox.TryPop(SessionA, out PlayerCommandFrame fresh));
        Assert.Equal(66UL, fresh.Sequence);
        serverWalkTicks++;
        Assert.Equal(clientWalkTicks, serverWalkTicks);
        Assert.Equal(0, inbox.GetSkipDebtTicks(SessionA));
        Assert.Equal(0, inbox.GetPendingTicks(SessionA));
    }

    // A ring that already held 8 ticks of latency when the send stalled starves 8 ticks less than
    // the stall, so 8 of the stall's idle commands are left after the skip. Kept, they held the
    // depth above the threshold under a steady sender until the shed merged the backlog and lost
    // the 7 walking ticks behind them (0.58-0.68 m in the loopback test). They are dropped instead:
    // the server simulated the walk ticks the client predicted at every ack, and nothing is shed.
    [Fact]
    public void SendStallOnTopOfAStandingBacklog_DropsTheIdleTail_AndLosesNoWalkTick()
    {
        BufferedPlayerCommandInbox inbox = new();
        Dictionary<ulong, bool> clientWalked = [];
        ulong produced = 0;
        int serverWalkTicks = 0;
        ulong lastApplied = 0;
        bool lastWalked = false;
        int starved = 0;
        // Before the shed would cut the standing backlog (ShedWindowTicks).
        const int StallStart = 21;
        const int StallEnd = StallStart + 64;

        void Produce(bool walk)
        {
            produced++;
            clientWalked[produced] = walk;
            inbox.Enqueue(Frame(SessionA, produced, forward: walk ? 1f : 0f));
        }

        // A catch-up burst at join leaves 8 ticks standing in the ring.
        for (int i = 0; i < 8; i++)
        {
            Produce(walk: true);
        }

        List<ulong> stalled = [];
        for (int tick = 1; tick <= StallEnd + 96; tick++)
        {
            if (tick >= StallStart && tick < StallEnd)
            {
                // The client walks the repeat window, then predicts idle; nothing is delivered.
                stalled.Add((ulong)tick);
            }
            else
            {
                if (tick == StallEnd)
                {
                    foreach (ulong _ in stalled)
                    {
                        Produce(walk: produced + 1 < (ulong)StallStart + (ulong)WorldSessionRuntime.MaxCommandRepeatTicks + 8);
                    }
                }

                Produce(walk: true);
            }

            if (inbox.TryPop(SessionA, out PlayerCommandFrame frame))
            {
                lastApplied = frame.Sequence;
                lastWalked = frame.MoveIntent.ForwardAxis != 0f;
                starved = 0;
                serverWalkTicks += lastWalked ? 1 : 0;
                int clientWalkTicks = clientWalked.Count(entry => entry.Key <= lastApplied && entry.Value);
                Assert.True(clientWalkTicks == serverWalkTicks, $"tick {tick}, ack {lastApplied}: client {clientWalkTicks} walk ticks, server {serverWalkTicks}");
            }
            else
            {
                starved++;
                serverWalkTicks += lastWalked && starved <= WorldSessionRuntime.MaxCommandRepeatTicks ? 1 : 0;
            }
        }

        Assert.InRange(inbox.GetPendingTicks(SessionA), 0, BufferedPlayerCommandInbox.ShedDepthThreshold);
    }

    // The client's switch to idle can land a tick or two off the server's (send timing). Within
    // SkipBoundaryToleranceTicks the stall is still skipped as a whole instead of replaying its
    // 46 idle ticks (whose latency the shed would cut along with fresh walking ticks). The backlog
    // arrives as a send-stalled client's outbound queue folds it: the sequence jumps are the
    // client-side stall that lets the tolerance apply (fix loop 2, S-C).
    [Theory]
    [InlineData(-2)]
    [InlineData(2)]
    public void SendStallBacklog_WithTheIdleSwitchSlightlyOff_IsStillSkippedAsAWhole(int switchOffset)
    {
        BufferedPlayerCommandInbox inbox = new();
        inbox.Enqueue(Frame(SessionA, 1, forward: 1f));
        Assert.True(inbox.TryPop(SessionA, out _));
        for (int i = 0; i < 64; i++)
        {
            Assert.False(inbox.TryPop(SessionA, out _));
        }

        ulong lastWalk = 1 + (ulong)(WorldSessionRuntime.MaxCommandRepeatTicks + switchOffset);
        Dream.Client.Networking.ClientOutboundCommandQueue outbound = new();
        for (ulong sequence = 2; sequence <= 66; sequence++)
        {
            outbound.TryEnqueue(Frame(SessionA, sequence, forward: sequence <= lastWalk || sequence == 66 ? 1f : 0f));
        }

        Assert.True(outbound.MergedCount > 0);
        while (outbound.TryDequeue(out PlayerCommandFrame sent))
        {
            inbox.Enqueue(sent);
        }

        Assert.True(inbox.TryPop(SessionA, out PlayerCommandFrame fresh));
        Assert.Equal(66UL, fresh.Sequence);
    }

    // Reviewer S3: debt a short backlog could not spend is kept for the frames after it, and
    // cleared on the first frame that does not match.
    [Fact]
    public void UnspentSkipDebt_IsKeptForTheNextFrames_AndClearedOnTheFirstMismatch()
    {
        BufferedPlayerCommandInbox inbox = new();
        inbox.Enqueue(Frame(SessionA, 1, forward: 1f));
        Assert.True(inbox.TryPop(SessionA, out _));
        for (int i = 0; i < 10; i++)
        {
            Assert.False(inbox.TryPop(SessionA, out _));
        }

        inbox.Enqueue(Frame(SessionA, 2, forward: 1f));
        inbox.Enqueue(Frame(SessionA, 3, forward: 1f));
        Assert.True(inbox.TryPop(SessionA, out PlayerCommandFrame first));
        Assert.Equal(3UL, first.Sequence);
        Assert.Equal(9, inbox.GetSkipDebtTicks(SessionA));

        for (ulong sequence = 4; sequence <= 6; sequence++)
        {
            inbox.Enqueue(Frame(SessionA, sequence, forward: 1f));
        }

        Assert.True(inbox.TryPop(SessionA, out PlayerCommandFrame second));
        Assert.Equal(6UL, second.Sequence);
        Assert.Equal(7, inbox.GetSkipDebtTicks(SessionA));

        inbox.Enqueue(Frame(SessionA, 7, forward: 1f, strafe: 1f));
        inbox.Enqueue(Frame(SessionA, 8, forward: 1f));
        Assert.True(inbox.TryPop(SessionA, out PlayerCommandFrame mismatch));
        Assert.Equal(7UL, mismatch.Sequence);
        Assert.Equal(0, inbox.GetSkipDebtTicks(SessionA));
        Assert.True(inbox.TryPop(SessionA, out PlayerCommandFrame next));
        Assert.Equal(8UL, next.Sequence);
    }

    // A jump inside the skipped ticks is popped at its own tick, where the client predicted it,
    // instead of being carried up to a second later; the debt waits for the frames after it.
    [Fact]
    public void PressInsideTheSkippedTicks_IsPoppedAtItsOwnTick()
    {
        BufferedPlayerCommandInbox inbox = new();
        inbox.Enqueue(Frame(SessionA, 1, forward: 1f));
        Assert.True(inbox.TryPop(SessionA, out _));
        for (int i = 0; i < 10; i++)
        {
            Assert.False(inbox.TryPop(SessionA, out _));
        }

        for (ulong sequence = 2; sequence <= 8; sequence++)
        {
            inbox.Enqueue(Frame(SessionA, sequence, forward: 1f, jump: sequence == 5));
        }

        List<PlayerCommandFrame> popped = PopAll(inbox, SessionA);
        Assert.Equal([5UL, 8UL], popped.Select(frame => frame.Sequence).ToArray());
        Assert.True(popped[0].MoveIntent.JumpPressed);
        Assert.False(popped[1].MoveIntent.JumpPressed);
    }

    // Reviewer S1: since NETR-4 a client sends one command per tick at any frame rate. At 28-40 fps
    // its commands arrive two or three at a time, which the old "two or more arrivals" heuristic
    // took for a render-rate sender and coalesced (and its ack then ran ahead of the simulation).
    // The sequence of a per-tick client never gains on the server's tick count, so the inbox keeps
    // the jitter buffer: every tick is simulated once (popped, or covered by a repeated tick) and
    // the ack never runs ahead of the ticks simulated since the first pop.
    [Theory]
    [InlineData(28)]
    [InlineData(30)]
    [InlineData(40)]
    public void PerTickClientAtLowFrameRate_KeepsTheJitterBuffer_AndTheAckNeverRunsAhead(int framesPerSecond)
    {
        BufferedPlayerCommandInbox inbox = new();
        const int ServerTicks = 64 * 10;
        double tickSeconds = 1d / SimulationClock.AuthoritativeTicksPerSecond;
        double frameSeconds = 1d / framesPerSecond;
        double nextFrame = 0.003;
        ulong produced = 0;
        ulong? firstAck = null;
        int firstAckTick = 0;
        int popped = 0;
        int repeated = 0;
        int maxArrivals = 0;
        for (int tick = 1; tick <= ServerTicks; tick++)
        {
            double now = tick * tickSeconds;
            int arrivals = 0;
            while (nextFrame <= now)
            {
                // Each render frame runs (and sends) every client tick elapsed by then.
                ulong due = (ulong)Math.Floor(nextFrame / tickSeconds);
                while (produced < due)
                {
                    produced++;
                    arrivals++;
                    inbox.Enqueue(Frame(SessionA, produced, forward: 1f));
                }

                nextFrame += frameSeconds;
            }

            maxArrivals = Math.Max(maxArrivals, arrivals);
            if (inbox.TryPop(SessionA, out PlayerCommandFrame frame))
            {
                firstAck ??= frame.Sequence;
                if (popped == 0)
                {
                    firstAckTick = tick;
                }

                popped++;
                Assert.True(
                    frame.Sequence - firstAck.Value <= (ulong)(tick - firstAckTick),
                    $"ack {frame.Sequence} ran ahead at tick {tick}");
            }
            else if (popped > 0)
            {
                repeated++;
            }

            Assert.False(inbox.IsFastSender(SessionA), $"{framesPerSecond} fps per-tick client taken for a render-rate sender at tick {tick}");
        }

        // Repeated ticks not yet matched by a command (the next pair is still on its way) are debt.
        int outstanding = inbox.GetSkipDebtTicks(SessionA);
        output.WriteLine($"{framesPerSecond} fps per-tick client: {produced} sent, up to {maxArrivals} per tick, {popped} popped, {repeated} repeated, {outstanding} repeated ahead of the commands");
        Assert.True(maxArrivals >= 2);
        Assert.InRange(outstanding, 0, 2);
        Assert.InRange(popped + repeated - outstanding, (int)produced - 3, (int)produced);
    }

    // M1: a stale frame from the old connection (sequence 38400) recreates the ring after the
    // release; the host removes the session again when the rejoin's join is applied, which must
    // reset everything (sequence, skip debt, starvation, fast-sender state) so the new connection's
    // sequence 1 is accepted.
    [Fact]
    public void Remove_AfterAStaleFrameRecreatedTheRing_AcceptsARejoinAtSequenceOne()
    {
        BufferedPlayerCommandInbox inbox = new();
        // A render-rate sender (two frames per tick) that then starves: fast-sender mode and skip debt.
        ulong sequence = 38000;
        for (int tick = 0; tick < 32; tick++)
        {
            inbox.Enqueue(Frame(SessionA, ++sequence, forward: 1f));
            inbox.Enqueue(Frame(SessionA, ++sequence, forward: 1f));
            Assert.True(inbox.TryPop(SessionA, out _));
        }

        Assert.True(inbox.IsFastSender(SessionA));
        Assert.False(inbox.TryPop(SessionA, out _));
        Assert.False(inbox.TryPop(SessionA, out _));
        Assert.True(inbox.GetSkipDebtTicks(SessionA) > 0);
        inbox.Remove(SessionA);

        inbox.Enqueue(Frame(SessionA, 38400, forward: 1f));
        Assert.True(inbox.Contains(SessionA));
        // Without the join-time Remove the stale ring rejects the new connection's sequence 1.
        inbox.Enqueue(Frame(SessionA, 1, forward: 1f));
        Assert.Equal(1, inbox.GetPendingTicks(SessionA));

        inbox.Remove(SessionA);
        Assert.False(inbox.Contains(SessionA));
        for (ulong rejoin = 1; rejoin <= 3; rejoin++)
        {
            inbox.Enqueue(Frame(SessionA, rejoin, forward: 1f));
        }

        Assert.Equal(3, inbox.GetPendingTicks(SessionA));
        Assert.Equal(0, inbox.GetSkipDebtTicks(SessionA));
        Assert.False(inbox.IsFastSender(SessionA));
        List<PlayerCommandFrame> popped = PopAll(inbox, SessionA);
        Assert.Equal([1UL, 2UL, 3UL], popped.Select(frame => frame.Sequence).ToArray());
        // PopAll's last, empty pop is the first starved tick after the rejoin: a fresh repeat window.
        Assert.Equal(1, inbox.GetSkipDebtTicks(SessionA));
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

    // Starting or stopping a sprint changes how far every tick of the same axes travels, so it is
    // a movement change and not a one-shot press: the outbound queue must not fold a sprint tick
    // into a run of walking ones, and the reconciler must not read it as "same as the ack".
    [Fact]
    public void HasSameMovement_IsFalse_WhenOnlySprintDiffers()
    {
        PlayerMoveIntent running = new(1f, 0f);
        PlayerMoveIntent sprinting = running with { SprintHeld = true };

        Assert.False(BufferedPlayerCommandInbox.HasSameMovement(running, sprinting));
        Assert.False(BufferedPlayerCommandInbox.HasSameMovement(sprinting, running));
        Assert.True(BufferedPlayerCommandInbox.HasSameMovement(sprinting, sprinting with { }));
    }

    // Sprint is held, not a press a tick consumes once, so it stays out of the one-shot set.
    [Fact]
    public void HasOneShotInput_IgnoresSprint()
    {
        PlayerCommandFrame sprinting = Frame(SessionA, 1, forward: 1f) with
        {
            MoveIntent = new PlayerMoveIntent(1f, 0f, SprintHeld: true)
        };

        Assert.False(BufferedPlayerCommandInbox.HasOneShotInput(sprinting));
        Assert.False(BufferedPlayerCommandInbox.HasTimedInput(sprinting));
    }

    private static PlayerCommandFrame Frame(
        PlayerSessionId session,
        ulong sequence,
        float forward = 0f,
        bool jump = false,
        bool primaryUse = false,
        float yawDelta = 0f,
        float strafe = 0f)
    {
        return new PlayerCommandFrame(
            session,
            sequence,
            PlayerModeSwitchIntent.None,
            new PlayerMoveIntent(forward, strafe, JumpPressed: jump),
            new PlayerLookIntent(yawDelta, 0f),
            primaryUse);
    }
}
