using Xunit;
using Xunit.Abstractions;
using Dream.Client.Networking;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;

namespace Dream.Tests.Multiplayer.Sessions;

// Wave 8 fix loop 2: the reviewer's second-review findings on BufferedPlayerCommandInbox and the
// client's outbound queue (S-A overflow merges moving a press, S-B stale skip debt, S-C the
// boundary tolerance without a client stall, N4 honest fast-sender flips, N5 merged-slot cap).
public class BufferedPlayerCommandInboxFixLoop2Tests(ITestOutputHelper output)
{
    private static readonly PlayerSessionId Session = new("fix-loop-2");

    // S-A, server: a 15-tick walk run that overflows the ring with a jump inside it. The jump frame
    // used to fold into the run before it and pop on the run's first tick (11 ticks early for a
    // jump on tick 12); it now pops at its own tick, and every tick of the run is still popped.
    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(12)]
    [InlineData(15)]
    [InlineData(16)]
    public void OverflowedBacklogRun_PopsAPressInsideItAtItsOwnTick(int jumpSequence)
    {
        BufferedPlayerCommandInbox inbox = new();
        inbox.Enqueue(Frame(1, forward: 1f));
        Assert.True(inbox.TryPop(Session, out _));
        for (ulong sequence = 2; sequence <= 16; sequence++)
        {
            inbox.Enqueue(Frame(sequence, forward: 1f, jump: sequence == (ulong)jumpSequence));
        }

        List<PlayerCommandFrame> popped = PopAll(inbox);
        Assert.Equal(Enumerable.Range(2, 15).Select(value => (ulong)value), popped.Select(frame => frame.Sequence));
        PlayerCommandFrame firstJump = popped.First(frame => frame.MoveIntent.JumpPressed);
        Assert.Equal((ulong)jumpSequence, firstJump.Sequence);
    }

    // S-A, server: a mode request is timed input too; it is not folded into the frames before it.
    [Fact]
    public void OverflowedBacklogRun_PopsAModeRequestAtItsOwnTick()
    {
        BufferedPlayerCommandInbox inbox = new();
        inbox.Enqueue(Frame(1, forward: 1f));
        Assert.True(inbox.TryPop(Session, out _));
        for (ulong sequence = 2; sequence <= 16; sequence++)
        {
            PlayerCommandFrame frame = Frame(sequence, forward: 1f);
            inbox.Enqueue(sequence == 12 ? frame with { RequestedMode = new PlayerModeSwitchIntent(PlayerInteractionMode.Combat) } : frame);
        }

        List<PlayerCommandFrame> popped = PopAll(inbox);
        Assert.Equal(12UL, popped.First(frame => frame.RequestedMode.Mode.HasValue).Sequence);
    }

    // S-A, server: with a press in every frame no pair keeps the presses' timing, so the inbox
    // falls back to the old rule (same movement) instead of growing past its capacity.
    [Fact]
    public void OverflowWithAPressInEveryFrame_FallsBackToTheOldRule()
    {
        BufferedPlayerCommandInbox inbox = new();
        for (ulong sequence = 1; sequence <= 20; sequence++)
        {
            inbox.Enqueue(Frame(sequence, forward: 1f, jump: true));
        }

        Assert.Equal(20, inbox.GetPendingTicks(Session));
        Assert.Equal(20, PopAll(inbox).Count);
    }

    // S-A, client: the outbound queue overflows during a send stall with a jump inside a walk run.
    // The merged frames keep the jump at its own tick once the inbox counts their sequence jumps.
    [Theory]
    [InlineData(12)]
    [InlineData(25)]
    [InlineData(40)]
    public void OutboundQueueOverflow_ThenInbox_PopsThePressAtItsOwnTick(int jumpSequence)
    {
        ClientOutboundCommandQueue queue = new();
        for (ulong sequence = 1; sequence <= 40; sequence++)
        {
            queue.TryEnqueue(Frame(sequence, forward: 1f, jump: sequence == (ulong)jumpSequence));
        }

        Assert.True(queue.MergedCount > 0);
        List<PlayerCommandFrame> sent = [];
        while (queue.TryDequeue(out PlayerCommandFrame frame))
        {
            sent.Add(frame);
        }

        // The frame carrying the jump starts at the tick after the frame sent before it.
        int jumpIndex = sent.FindIndex(frame => frame.MoveIntent.JumpPressed);
        ulong firstTickOfJumpFrame = jumpIndex == 0 ? 1UL : sent[jumpIndex - 1].Sequence + 1;
        Assert.Equal((ulong)jumpSequence, firstTickOfJumpFrame);

        BufferedPlayerCommandInbox inbox = new();
        foreach (PlayerCommandFrame frame in sent)
        {
            inbox.Enqueue(frame);
        }

        List<PlayerCommandFrame> popped = PopAll(inbox);
        Assert.Equal((ulong)jumpSequence, popped.First(frame => frame.MoveIntent.JumpPressed).Sequence);
    }

    // S-B: a frame stall whose catch-up was capped leaves debt the one-frame-per-tick arrivals
    // after it can never spend (the newest pending tick is never skipped). Minutes later a 30 fps
    // client's [walk, idle] pairs matched that stale debt and real walk ticks were skipped. The
    // debt is now cleared after a ring of pops without a starved tick, or as soon as the frame
    // about to be popped does not match.
    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public void StaleDebtAfterACappedFrameStall_NeverSkipsALaterWalkTick(int catchUpTicks)
    {
        BufferedPlayerCommandInbox inbox = new();
        ulong sequence = 0;
        for (int tick = 0; tick < 32; tick++)
        {
            inbox.Enqueue(Frame(++sequence, forward: 1f));
            Assert.True(inbox.TryPop(Session, out _));
        }

        // The client loop freezes for a second: the server repeats the walk, then idles.
        for (int tick = 0; tick < 64; tick++)
        {
            Assert.False(inbox.TryPop(Session, out _));
        }

        Assert.Equal(64, inbox.GetSkipDebtTicks(Session));

        // Capped catch-up burst, then one walking frame per tick.
        for (int i = 0; i < catchUpTicks; i++)
        {
            inbox.Enqueue(Frame(++sequence, forward: 1f));
        }

        Assert.True(inbox.TryPop(Session, out _));
        for (int tick = 0; tick < 64 * 10; tick++)
        {
            inbox.Enqueue(Frame(++sequence, forward: 1f));
            Assert.True(inbox.TryPop(Session, out _));
        }

        Assert.Equal(0, inbox.GetSkipDebtTicks(Session));

        // A 30 fps client whose input alternates: [walk, idle] pairs, two per two ticks.
        HashSet<ulong> walkSent = [];
        HashSet<ulong> walkPopped = [];
        for (int pair = 0; pair < 64; pair++)
        {
            ulong walk = ++sequence;
            walkSent.Add(walk);
            inbox.Enqueue(Frame(walk, forward: 1f));
            inbox.Enqueue(Frame(++sequence, forward: 0f));
            for (int tick = 0; tick < 2; tick++)
            {
                Assert.True(inbox.TryPop(Session, out PlayerCommandFrame frame));
                if (frame.MoveIntent.ForwardAxis != 0f)
                {
                    walkPopped.Add(frame.Sequence);
                }
            }
        }

        Assert.Equal(walkSent.Order(), walkPopped.Order());
    }

    // S-B: the frame about to be popped clears debt it does not match, even when it is the only
    // pending tick (the skip loop never looks at it).
    [Fact]
    public void SinglePendingFrameThatDoesNotMatch_ClearsTheDebt()
    {
        BufferedPlayerCommandInbox inbox = new();
        inbox.Enqueue(Frame(1, forward: 1f));
        Assert.True(inbox.TryPop(Session, out _));
        for (int tick = 0; tick < 40; tick++)
        {
            Assert.False(inbox.TryPop(Session, out _));
        }

        Assert.Equal(40, inbox.GetSkipDebtTicks(Session));
        inbox.Enqueue(Frame(2, forward: 0f, strafe: 1f));
        Assert.True(inbox.TryPop(Session, out PlayerCommandFrame frame));
        Assert.Equal(2UL, frame.Sequence);
        Assert.Equal(0, inbox.GetSkipDebtTicks(Session));
    }

    // S-C, forward case: a pure network delay of 30 ticks, no client stall (consecutive sequences).
    // The client walked 18 ticks into it and stopped. The server repeated the walk for 16 ticks
    // and idled; the old 4-tick tolerance also skipped the last 2 walk ticks (0.19 m short). Now
    // only exact matches are skipped and the server ends with every walk tick the client made.
    [Fact]
    public void NetworkDelay_ClientWalkedPastTheRepeatWindow_ServerWalksEveryTick()
    {
        const int DelayTicks = 30;
        const int ClientWalkTicks = 18;
        ServerModel server = new();
        server.Deliver(Frame(1, forward: 1f));
        server.Tick();
        for (int tick = 0; tick < DelayTicks; tick++)
        {
            server.Tick();
        }

        for (ulong sequence = 2; sequence <= 1 + DelayTicks; sequence++)
        {
            server.Deliver(Frame(sequence, forward: sequence <= 1 + ClientWalkTicks ? 1f : 0f));
        }

        for (ulong sequence = 2 + DelayTicks; sequence <= 200; sequence++)
        {
            server.Tick();
            server.Deliver(Frame(sequence, forward: 0f));
        }

        server.DrainIdle();
        output.WriteLine($"network delay, client walked 1 + {ClientWalkTicks}: server walked {server.WalkTicks}");
        Assert.Equal(1 + ClientWalkTicks, server.WalkTicks);
    }

    // S-C, reverse case: the client stopped 2 ticks before the server's repeat window ended. The
    // repeated ticks are already simulated, so the server is 2 ticks ahead either way, but without
    // a client stall the idle backlog no longer counts as matching (it is replayed, and nothing
    // more is skipped on a guess).
    [Fact]
    public void NetworkDelay_ClientStoppedBeforeTheRepeatWindowEnded_DebtIsClearedNotSpentOnAGuess()
    {
        const int DelayTicks = 30;
        const int ClientWalkTicks = 14;
        BufferedPlayerCommandInbox inbox = new();
        inbox.Enqueue(Frame(1, forward: 1f));
        Assert.True(inbox.TryPop(Session, out _));
        for (int tick = 0; tick < DelayTicks; tick++)
        {
            Assert.False(inbox.TryPop(Session, out _));
        }

        for (ulong sequence = 2; sequence <= 1 + DelayTicks; sequence++)
        {
            inbox.Enqueue(Frame(sequence, forward: sequence <= 1 + ClientWalkTicks ? 1f : 0f));
        }

        Assert.True(inbox.TryPop(Session, out PlayerCommandFrame first));
        // The 14 walk ticks matched the repeats and were skipped; the first idle one, with 2
        // repeat ticks still owed, is popped instead of skipped on the tolerance.
        Assert.Equal(2UL + ClientWalkTicks, first.Sequence);
        Assert.Equal(0, inbox.GetSkipDebtTicks(Session));
    }

    // S-C, with a client stall: the same early switch inside a send-stalled backlog (the client's
    // queue folded it, so its sequence jumps) is still tolerated and the stall skipped as a whole.
    [Fact]
    public void SendStall_ClientStoppedBeforeTheRepeatWindowEnded_ToleranceStillApplies()
    {
        const int StallTicks = 30;
        const int ClientWalkTicks = 14;
        BufferedPlayerCommandInbox inbox = new();
        inbox.Enqueue(Frame(1, forward: 1f));
        Assert.True(inbox.TryPop(Session, out _));
        for (int tick = 0; tick < StallTicks; tick++)
        {
            Assert.False(inbox.TryPop(Session, out _));
        }

        // Folded runs, as ClientOutboundCommandQueue sends them.
        inbox.Enqueue(Frame(1 + ClientWalkTicks, forward: 1f));
        inbox.Enqueue(Frame(1 + StallTicks, forward: 0f));
        inbox.Enqueue(Frame(2 + StallTicks, forward: 1f));
        Assert.True(inbox.TryPop(Session, out PlayerCommandFrame fresh));
        Assert.Equal(2UL + StallTicks, fresh.Sequence);
    }

    // N4: an overloaded server ticking at 32 Hz sees an honest 64 Hz per-tick client's lead rise
    // every tick and flips it into fast-sender mode; that flip is counted as an honest one.
    [Fact]
    public void OverloadedServer_FlippingAnHonestPerTickClient_IsCounted()
    {
        long[] now = [0];
        const long Frequency = 1_000_000;
        BufferedPlayerCommandInbox inbox = new(() => now[0], Frequency);
        ulong sequence = 0;
        for (int tick = 0; tick < 64; tick++)
        {
            // 1/32 s per server tick: two 64 Hz client ticks arrive per server tick.
            now[0] += Frequency / 32;
            inbox.Enqueue(Frame(++sequence, forward: 1f));
            inbox.Enqueue(Frame(++sequence, forward: 1f));
            inbox.TryPop(Session, out _);
        }

        Assert.True(inbox.IsFastSender(Session));
        Assert.Equal(1, inbox.FastSenderEntries);
        Assert.Equal(1, inbox.HonestSenderFastModeEntries);
    }

    // N4: a legacy per-render client at 144 Hz against a 64 Hz server is a real fast sender; it is
    // not counted as an honest flip.
    [Fact]
    public void RenderRateSender_AtFullServerRate_IsNotCountedAsAnHonestFlip()
    {
        long[] now = [0];
        const long Frequency = 1_000_000;
        BufferedPlayerCommandInbox inbox = new(() => now[0], Frequency);
        ulong sequence = 0;
        double owed = 0d;
        for (int tick = 0; tick < 64; tick++)
        {
            now[0] += Frequency / 64;
            owed += 144d / 64d;
            while (owed >= 1d)
            {
                owed -= 1d;
                inbox.Enqueue(Frame(++sequence, forward: 1f));
            }

            inbox.TryPop(Session, out _);
        }

        Assert.True(inbox.IsFastSender(Session));
        Assert.Equal(1, inbox.FastSenderEntries);
        Assert.Equal(0, inbox.HonestSenderFastModeEntries);
    }

    // N5: overflow merges of frames that each stand for MaxTicksPerFrame ticks stop growing the
    // merged slot at MaxMergedSlotTicks.
    [Fact]
    public void MergedSlotTicks_AreCappedAtMaxMergedSlotTicks()
    {
        BufferedPlayerCommandInbox inbox = new();
        ulong sequence = 1;
        inbox.Enqueue(Frame(sequence, forward: 1f));
        for (int i = 0; i < 40; i++)
        {
            sequence += BufferedPlayerCommandInbox.MaxTicksPerFrame;
            inbox.Enqueue(Frame(sequence, forward: 1f));
        }

        int pending = inbox.GetPendingTicks(Session);
        output.WriteLine($"pending ticks {pending}");
        Assert.True(
            pending <= BufferedPlayerCommandInbox.MaxMergedSlotTicks + ((BufferedPlayerCommandInbox.Capacity - 1) * BufferedPlayerCommandInbox.MaxTicksPerFrame),
            $"pending ticks {pending}");
        // The pops count back from the newest sequence over exactly the capped ticks.
        Assert.True(inbox.TryPop(Session, out PlayerCommandFrame first));
        Assert.Equal(sequence - (ulong)(pending - 1), first.Sequence);
    }

    private static List<PlayerCommandFrame> PopAll(BufferedPlayerCommandInbox inbox)
    {
        List<PlayerCommandFrame> popped = [];
        while (inbox.TryPop(Session, out PlayerCommandFrame frame))
        {
            popped.Add(frame);
            Assert.True(popped.Count <= 100_000);
        }

        return popped;
    }

    private static PlayerCommandFrame Frame(ulong sequence, float forward = 0f, bool jump = false, float strafe = 0f)
    {
        return new PlayerCommandFrame(
            Session,
            sequence,
            PlayerModeSwitchIntent.None,
            new PlayerMoveIntent(forward, strafe, JumpPressed: jump),
            new PlayerLookIntent(0f, 0f),
            PrimaryUsePressed: false);
    }

    // The runtime's NETR-3 rule over the inbox: counts the ticks the seat walks.
    private sealed class ServerModel
    {
        private readonly BufferedPlayerCommandInbox _inbox = new();
        private PlayerCommandFrame? _last;
        private int _starved;

        public int WalkTicks { get; private set; }

        public void Deliver(PlayerCommandFrame frame) => _inbox.Enqueue(frame);

        public void Tick()
        {
            PlayerCommandFrame simulated;
            if (_inbox.TryPop(Session, out PlayerCommandFrame popped))
            {
                _last = popped;
                _starved = 0;
                simulated = popped;
            }
            else if (_last.HasValue)
            {
                _starved++;
                simulated = _starved > WorldSessionRuntime.MaxCommandRepeatTicks
                    ? WorldSessionRuntime.CreateStarvedCommandFrame(_last.Value)
                    : _last.Value;
            }
            else
            {
                return;
            }

            WalkTicks += simulated.MoveIntent.ForwardAxis != 0f ? 1 : 0;
        }

        public void DrainIdle()
        {
            while (_inbox.GetPendingTicks(Session) > 0)
            {
                Tick();
            }
        }
    }
}
