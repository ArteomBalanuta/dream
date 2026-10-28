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
// The error is measured, like the reconciler, only when the ack advances.
//
// Findings at the wave 8 tip (e4c6930), before the fix loop:
// - FIFO 16: the queue folded 48 stall commands into one frame with a sequence jump of 48, which
//   the inbox stored as ONE slot standing for ONE tick; it skipped 16 leading ticks and popped the
//   frame with the full jumped sequence. The server simulated 16 repeated + 1 walk ticks for 64
//   client walk ticks: an error of 47 ticks (~4.4 m, the measured 4.53 m snap).
// - A bigger client FIFO (no merge) or an inbox that counts a sequence gap as N ticks did not fix it
//   on its own: the replayable backlog is 48 ticks; the client keeps sending one frame per tick, so
//   the depth never drains and the elastic shed (ShedWindowTicks = 32) merges it down to one frame,
//   dropping the same ~47 ticks a bit later. Both still hold for a client that walks through the
//   stall (the first two tests): that is why the client has to idle.
//
// The fix (wave 8 fix loop 1), end to end:
// - The client predicts AND sends idle once its oldest unsent command is MaxCommandRepeatTicks old
//   (ClientCommandTickStream), which is what the server simulates for those ticks.
// - The outbound queue folds the backlog into runs of equal movement, and the inbox counts a
//   folded frame's sequence jump as that many ticks (reviewer S2).
// - The inbox skips every starved tick, not just the 16 repeated ones: matching the repeated
//   movement for the repeat window, then idle; a switch a few ticks off the server's is tolerated.
// Modelled: the error at every new ack is 0 ticks with an exact switch, at most the offset with a
// switch off by up to 3 ticks, and nothing is shed.
public class Wave8SendStallInboxModelTests(ITestOutputHelper output)
{
    private const int WalkBeforeStall = 32;
    private const int StallTicks = 64;
    private const int TicksAfterStall = 160;
    private static readonly PlayerSessionId Session = new("w8-model");
    private static readonly Vector3 Facing = Vector3.UnitZ;

    // A client that walks through the stall: the inbox now counts the folded frame's 50 ticks and
    // skips the 16 repeated ones, but the other 34 walk ticks mismatch the server's idle ticks, so
    // they are replayed on top of the client's fresh commands and the shed cuts them.
    [Fact]
    public void Fifo16_ClientWalkingThroughTheStall_ReplaysTheBacklogAndShedsIt_ServerEndsFortySevenWalkTicksBehind()
    {
        ModelResult result = Run(fifoCapacity: ClientOutboundCommandQueue.DefaultCapacity, clientIdlesWhenSendStalled: false);
        output.WriteLine(result.ToString());

        // 50 here (no frame held by a blocked send), 48 on the real loopback stack.
        Assert.InRange(result.LargestSequenceJump, 46UL, 52UL);
        Assert.True(result.Sheds > 0, result.ToString());
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
    public void ClientPredictsIdleAfterSixteenUnsentTicks_ServerAgreesAtEveryAck()
    {
        ModelResult result = Run(fifoCapacity: ClientOutboundCommandQueue.DefaultCapacity, clientIdlesWhenSendStalled: true);
        output.WriteLine(result.ToString());

        // The folded walk run is followed by the folded idle run: the wire still jumps.
        Assert.True(result.LargestSequenceJump > 16, result.ToString());
        Assert.Equal(0, result.Sheds);
        Assert.Equal(0, result.MaxErrorTicks);
        Assert.Equal(0, result.FinalErrorTicks);
    }

    // The client's estimate of where the server switched to idle can be a few ticks off.
    [Theory]
    [InlineData(-3)]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(3)]
    public void ClientIdleSwitchOffByAFewTicks_ErrsByAtMostThoseTicks(int idleSwitchOffset)
    {
        ModelResult result = Run(fifoCapacity: ClientOutboundCommandQueue.DefaultCapacity, clientIdlesWhenSendStalled: true, idleSwitchOffset);
        output.WriteLine(result.ToString());

        Assert.Equal(0, result.Sheds);
        Assert.InRange(result.MaxErrorTicks, 0, Math.Abs(idleSwitchOffset));
        Assert.InRange(result.FinalErrorTicks, -Math.Abs(idleSwitchOffset), Math.Abs(idleSwitchOffset));
    }

    private static ModelResult Run(int fifoCapacity, bool clientIdlesWhenSendStalled, int idleSwitchOffset = 0)
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
        ulong lastComparedAck = 0;
        bool stalled = false;

        int totalTicks = WalkBeforeStall + StallTicks + TicksAfterStall;
        for (ulong tick = 1; tick <= (ulong)totalTicks; tick++)
        {
            stalled = tick > WalkBeforeStall && tick <= WalkBeforeStall + StallTicks;

            // Client produces exactly one command per tick (NETR-4).
            bool walk = true;
            if (clientIdlesWhenSendStalled && stalled && oldestUnsent != 0 && (long)(tick - oldestUnsent) >= WorldSessionRuntime.MaxCommandRepeatTicks + idleSwitchOffset)
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
            // The reconciler compares only a new ack: a repeated one comes from a server that kept
            // simulating without a command (ClientPredictionReconciler._lastComparedSequence).
            if (ack != lastComparedAck)
            {
                lastComparedAck = ack;
                maxError = Math.Max(maxError, Math.Abs(error));
            }
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
