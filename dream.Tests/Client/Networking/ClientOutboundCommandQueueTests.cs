using OpenTK.Mathematics;
using Xunit;
using Dream.Client.Networking;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using Dream.Tests.Utilities;

namespace Dream.Tests.Client.Networking;

// NETR-4: the client's outbound commands are a bounded FIFO of 16 that merges, not drops, on
// overflow.
public class ClientOutboundCommandQueueTests
{
    private static readonly PlayerSessionId Session = new("session-a");

    [Fact]
    public void WithinCapacity_DequeuesEveryCommandInOrder()
    {
        ClientOutboundCommandQueue queue = new();
        for (ulong sequence = 1; sequence <= 16; sequence++)
        {
            Assert.True(queue.TryEnqueue(Command(sequence, forward: sequence)));
        }

        for (ulong sequence = 1; sequence <= 16; sequence++)
        {
            Assert.True(queue.TryDequeue(out PlayerCommandFrame frame));
            Assert.Equal(sequence, frame.Sequence);
            Assert.Equal(sequence, frame.MoveIntent.ForwardAxis);
        }

        Assert.False(queue.TryDequeue(out _));
        Assert.Equal(0, queue.MergedCount);
    }

    // Overflow folds the oldest command into the next: the newest sequence, axes and facing win,
    // look deltas add up, and the presses from any merged command survive.
    [Fact]
    public void Overflow_MergesOldestKeepingOneShotsAndNewestSequence()
    {
        ClientOutboundCommandQueue queue = new(capacity: 4);
        queue.TryEnqueue(Command(1, forward: 0.1f) with
        {
            MoveIntent = new PlayerMoveIntent(0.1f, 0f, JumpPressed: true),
            RequestedMode = new PlayerModeSwitchIntent(PlayerInteractionMode.Combat),
            LookIntent = new PlayerLookIntent(1f, 0.5f, Vector3.UnitX)
        });
        queue.TryEnqueue(Command(2, forward: 0.2f) with { PrimaryUsePressed = true, LookIntent = new PlayerLookIntent(2f, 0f, null) });
        queue.TryEnqueue(Command(3, forward: 0.3f) with { MoveIntent = new PlayerMoveIntent(0.3f, 0f, ToggleNoclipPressed: true) });
        queue.TryEnqueue(Command(4, forward: 0.4f));
        queue.TryEnqueue(Command(5, forward: 0.5f));
        queue.TryEnqueue(Command(6, forward: 0.6f) with { LookIntent = new PlayerLookIntent(0f, 0f, -Vector3.UnitZ) });

        Assert.Equal(4, queue.Count);
        Assert.Equal(2, queue.MergedCount);
        Assert.True(queue.TryDequeue(out PlayerCommandFrame merged));
        Assert.Equal(3ul, merged.Sequence);
        Assert.Equal(0.3f, merged.MoveIntent.ForwardAxis);
        Assert.True(merged.MoveIntent.JumpPressed);
        Assert.True(merged.MoveIntent.ToggleNoclipPressed);
        Assert.True(merged.PrimaryUsePressed);
        Assert.Equal(PlayerInteractionMode.Combat, merged.RequestedMode.Mode);
        Assert.Equal(3f, merged.LookIntent.YawDelta);
        Assert.Equal(0.5f, merged.LookIntent.PitchDelta);
        Assert.Equal(Vector3.UnitX, merged.LookIntent.Facing);

        Assert.Equal([4ul, 5ul, 6ul], DrainSequences(queue));
    }

    [Fact]
    public void NotNewerSequence_IsIgnoredEvenAfterTheQueueDrained()
    {
        ClientOutboundCommandQueue queue = new();
        Assert.True(queue.TryEnqueue(Command(7, forward: 1f)));
        Assert.True(queue.TryDequeue(out _));

        Assert.False(queue.TryEnqueue(Command(7, forward: 1f)));
        Assert.False(queue.TryEnqueue(Command(3, forward: 1f)));
        Assert.True(queue.TryEnqueue(Command(8, forward: 1f)));
        Assert.Equal([8ul], DrainSequences(queue));
    }

    [Fact]
    public void EnqueueAndDequeue_AllocateNothing_EvenWhenMerging()
    {
        ClientOutboundCommandQueue queue = new(capacity: 4);
        ulong sequence = 0;
        PlayerCommandFrame template = Command(0, forward: 1f);

        AllocationAssert.AtMost(0, () =>
        {
            // Six in, four out: two merges per iteration.
            for (int i = 0; i < 6; i++)
            {
                queue.TryEnqueue(template with { Sequence = ++sequence });
            }

            while (queue.TryDequeue(out _))
            {
            }
        });
    }

    private static ulong[] DrainSequences(ClientOutboundCommandQueue queue)
    {
        List<ulong> sequences = [];
        while (queue.TryDequeue(out PlayerCommandFrame frame))
        {
            sequences.Add(frame.Sequence);
        }

        return sequences.ToArray();
    }

    private static PlayerCommandFrame Command(ulong sequence, float forward)
    {
        return new PlayerCommandFrame(
            Session,
            sequence,
            PlayerModeSwitchIntent.None,
            new PlayerMoveIntent(forward, 0f),
            PlayerLookIntent.None,
            PrimaryUsePressed: false);
    }
}
