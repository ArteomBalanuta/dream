using Dream.Client.Networking;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Multiplayer;

// Wave 8 tester: a tick-exact model of a 1 s client send stall through the real
// ClientOutboundCommandQueue (NETR-4) and the real BufferedPlayerCommandInbox (NETR-5), with the
// runtime's NETR-3 starvation rule (repeat the last command for MaxCommandRepeatTicks, then idle).
// No sockets, no physics: a tick "walks" when the server simulates a walking frame, and the
// position error at an ack is (client walk ticks up to the acked sequence) - (server walk ticks).
// One walk tick is ~0.094 m at the default tuning (the loopback test measured 4.53 m for 48 ticks).
//
// Findings (see the loopback test Wave8CommandStallCorrectionLoopbackTests for the real stack):
// - Current (FIFO 16): the queue folds 48 stall commands into one frame with a sequence jump of
//   48. The inbox stores it as ONE slot standing for ONE tick (BufferedPlayerCommandInbox.cs:229),
//   SkipRepeatedTicks then skips 16 leading ticks and the frame pops with the full jumped sequence
//   (line 253). The server simulated 16 repeated + 1 walk ticks for 64 client walk ticks: an error
//   of 47 ticks (~4.4 m, the measured 4.53 m snap).
// - A bigger client FIFO (no merge) or an inbox that counts a sequence gap as N ticks (the same
//   thing for constant input) does NOT fix it on its own: the replayable backlog is 48 ticks; the
//   client keeps sending one frame per tick, so the depth never drains and the elastic shed
//   (ShedWindowTicks = 32) merges it down to one frame, dropping the same ~47 ticks a bit later.
// - What the server really did during the stall was: walk 16 ticks, then idle 48. A client that
//   predicts that too (idle once its oldest unsent command is MaxCommandRepeatTicks old) agrees with
//   the server for the stall; modelled: max error 16 ticks (~1.5 m, smoothed, no snap), 7 at the end.
public class Wave8SendStallInboxModelTests(ITestOutputHelper output)
{
    private const int WalkBeforeStall = 32;
    private const int StallTicks = 64;
    private const int TicksAfterStall = 160;
    private static readonly PlayerSessionId Session = new("w8-model");
    private static readonly Vector3 Facing = Vector3.UnitZ;

    [Fact]
    public void CurrentFifo16_MergedStallFrameCountsAsOneTick_ServerEndsFortySevenWalkTicksBehind()
    {
        ModelResult result = Run(fifoCapacity: ClientOutboundCommandQueue.DefaultCapacity, clientIdlesWhenSendStalled: false);
        output.WriteLine(result.ToString());

        // 50 here (no frame held by a blocked send), 48 on the real loopback stack.
        Assert.InRange(result.LargestSequenceJump, 46UL, 52UL);
        Assert.InRange(result.MaxErrorTicks, 40, 52);
        Assert.InRange(result.FinalErrorTicks, 40, 52);
    }

    [Fact]
    public void LargerFifo_NoMerge_StillShedsTheBacklogAndEndsAsFarBehind()
    {
        ModelResult result = Run(fifoCapacity: 128, clientIdlesWhenSendStalled: false);
        output.WriteLine(result.ToString());

        Assert.Equal(1UL, result.LargestSequenceJump);
        Assert.True(result.Sheds > 0, result.ToString());
        Assert.InRange(result.FinalErrorTicks, 30, 50);
    }

    [Fact]
    public void ClientPredictsIdleAfterSixteenUnsentTicks_StaysBelowSnapDistance()
    {
        ModelResult result = Run(fifoCapacity: ClientOutboundCommandQueue.DefaultCapacity, clientIdlesWhenSendStalled: true);
        output.WriteLine(result.ToString());

        // Not zero: SkipRepeatedTicks only skips leading frames that match the REPEATED frame (at most
        // 16), so the idle part of the backlog is replayed and later shed while the client walks
        // again. Measured max 16 ticks (~1.5 m, a smooth correction) and 7 at the end, against 48
        // (~4.5 m snap) today. Making the inbox also skip backlog frames for the ticks the runtime
        // idled (debt = every starved tick, matching idle beyond the repeat window) would remove it.
        Assert.True(result.MaxErrorTicks < 32, result.ToString());
        Assert.InRange(result.FinalErrorTicks, -10, 10);
    }

    private static ModelResult Run(int fifoCapacity, bool clientIdlesWhenSendStalled)
    {
        ClientOutboundCommandQueue queue = new(fifoCapacity);
        BufferedPlayerCommandInbox inbox = new();
        PlayerSessionId[] sessions = [Session];
        List<PlayerCommandFrame> popped = [];

        // Client side: whether the command it predicted for each tick walked.
        Dictionary<ulong, bool> clientWalked = [];
        PlayerCommandFrame? lastApplied = null;
        int ticksSinceCommand = 0;
        int serverWalkTicks = 0;
        ulong ack = 0;
        ulong lastWireSequence = 0;
        ulong largestJump = 0;
        int maxError = 0;
        int sheds = 0;
        int previousPending = 0;
        long walkUpToAck = 0;
        ulong oldestUnsent = 0;
        bool stalled = false;

        int totalTicks = WalkBeforeStall + StallTicks + TicksAfterStall;
        for (ulong tick = 1; tick <= (ulong)totalTicks; tick++)
        {
            stalled = tick > WalkBeforeStall && tick <= WalkBeforeStall + StallTicks;

            // Client produces exactly one command per tick (NETR-4).
            bool walk = true;
            if (clientIdlesWhenSendStalled && stalled && oldestUnsent != 0 && tick - oldestUnsent > (ulong)WorldSessionRuntime.MaxCommandRepeatTicks)
            {
                walk = false;
            }

            PlayerCommandFrame command = walk ? Walk(tick) : WorldSessionRuntime.CreateStarvedCommandFrame(Walk(tick)) with { Sequence = tick };
            clientWalked[tick] = walk;
            queue.TryEnqueue(command);
            if (stalled && oldestUnsent == 0)
            {
                oldestUnsent = tick;
            }

            // The send loop delivers everything queued unless the transport is stalled.
            if (!stalled)
            {
                oldestUnsent = 0;
                while (queue.TryDequeue(out PlayerCommandFrame sent))
                {
                    if (lastWireSequence != 0)
                    {
                        largestJump = Math.Max(largestJump, sent.Sequence - lastWireSequence);
                    }

                    lastWireSequence = sent.Sequence;
                    inbox.Enqueue(sent);
                }
            }

            // Server tick: pop one frame, or repeat then idle (NETR-3).
            inbox.DrainInto(sessions, popped);
            PlayerCommandFrame simulated;
            if (popped.Count == 1)
            {
                lastApplied = popped[0];
                ack = popped[0].Sequence;
                ticksSinceCommand = 0;
                simulated = popped[0];
            }
            else if (lastApplied.HasValue)
            {
                simulated = ticksSinceCommand > WorldSessionRuntime.MaxCommandRepeatTicks
                    ? WorldSessionRuntime.CreateStarvedCommandFrame(lastApplied.Value)
                    : lastApplied.Value;
            }
            else
            {
                continue;
            }

            ticksSinceCommand++;
            if (simulated.MoveIntent.ForwardAxis != 0f)
            {
                serverWalkTicks++;
            }

            int pending = inbox.GetPendingTicks(Session);
            if (previousPending > BufferedPlayerCommandInbox.ShedDepthThreshold + 1 && pending <= 1 && popped.Count == 1)
            {
                sheds++;
            }

            previousPending = pending;
            walkUpToAck = clientWalked.Where(entry => entry.Key <= ack && entry.Value).LongCount();
            int error = (int)(walkUpToAck - serverWalkTicks);
            maxError = Math.Max(maxError, Math.Abs(error));
        }

        return new ModelResult(fifoCapacity, clientIdlesWhenSendStalled, largestJump, maxError, (int)(walkUpToAck - serverWalkTicks), sheds, ack, serverWalkTicks);
    }

    private static PlayerCommandFrame Walk(ulong sequence)
    {
        return new PlayerCommandFrame(
            Session,
            sequence,
            PlayerModeSwitchIntent.None,
            new PlayerMoveIntent(1f, 0f),
            new PlayerLookIntent(0f, 0f, Facing),
            PrimaryUsePressed: false);
    }

    private sealed record ModelResult(
        int FifoCapacity,
        bool ClientIdlesWhenSendStalled,
        ulong LargestSequenceJump,
        int MaxErrorTicks,
        int FinalErrorTicks,
        int Sheds,
        ulong FinalAck,
        int ServerWalkTicks);
}
