using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;

namespace Dream.Client.Networking;

// The client's outbound command frames, oldest first, waiting for the send loop (NETR-4).
//
// The client produces exactly one command per prediction tick, and every one of them is sent, in
// order, so a server that buffers commands (NETR-5's per-session ring) can simulate them one per
// tick. The queue used to be a single latest-wins slot, so a send backlog reached the server as
// one merged frame and the server lost every tick but the newest.
//
// It holds at most Capacity frames. When a send stall overflows it, two neighbouring frames merge
// into one instead of one being dropped: the newer axes, facing and sequence win, look deltas add
// up, and one-shot inputs (jump, freeze, noclip toggle, primary use, a mode request) are kept, so
// a press made during the stall still reaches the server. The merged frame keeps the newer
// sequence, and the server counts the sequence jump as that many ticks of the frame's movement
// (BufferedPlayerCommandInbox), starting at the tick after the previous frame's: the merged frame
// is simulated from the older frame's tick, and its presses land there. So the oldest neighbours
// with the same movement axes whose newer frame has no one-shot press or mode request merge first:
// a stall backlog made of runs (walk for the NETR-3 repeat window, then idle, see
// ClientCommandTickStream) folds into one frame per run, every tick keeps the movement the client
// predicted for it, and a press stays at its own tick (a press frame folds into the frames after
// it, never into the one before). Only when no such pair exists do the oldest neighbours with the
// same axes merge (a press then moves earlier, to the start of the merged run), and with none of
// those, the oldest two. Sequences only ever increase: a frame whose sequence is not newer than the
// last one queued is ignored.
//
// Thread-safe: the render thread enqueues, the send loop dequeues. No allocation after
// construction.
public sealed class ClientOutboundCommandQueue
{
    public const int DefaultCapacity = 16;

    private readonly object _sync = new();
    private readonly PlayerCommandFrame[] _frames;
    private int _head;
    private int _count;
    private bool _hasLastSequence;
    private ulong _lastSequence;
    private long _mergedCount;

    public ClientOutboundCommandQueue(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        _frames = new PlayerCommandFrame[capacity];
    }

    public int Capacity => _frames.Length;

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _count;
            }
        }
    }

    public bool HasPending => Count > 0;

    // Frames folded into a neighbour because the queue was full.
    public long MergedCount => Interlocked.Read(ref _mergedCount);

    public bool TryEnqueue(PlayerCommandFrame frame)
    {
        lock (_sync)
        {
            if (_hasLastSequence && frame.Sequence <= _lastSequence)
            {
                return false;
            }

            _hasLastSequence = true;
            _lastSequence = frame.Sequence;
            if (_count == _frames.Length)
            {
                MergeOldestMatchingNeighbours();
                Interlocked.Increment(ref _mergedCount);
            }

            _frames[(_head + _count) % _frames.Length] = frame;
            _count++;
            return true;
        }
    }

    public bool TryDequeue(out PlayerCommandFrame frame)
    {
        lock (_sync)
        {
            if (_count == 0)
            {
                frame = default;
                return false;
            }

            frame = _frames[_head];
            _frames[_head] = default;
            _head = (_head + 1) % _frames.Length;
            _count--;
            return true;
        }
    }

    // Folds the oldest frame whose next neighbour moves the same way and has no press or mode
    // request into that neighbour (the merged frame keeps the newer sequence); with no such pair,
    // the oldest frame whose next neighbour moves the same way; with none, the oldest into the next.
    private void MergeOldestMatchingNeighbours()
    {
        int index = FindMergePair(keepTimedInput: true);
        if (index < 0)
        {
            index = FindMergePair(keepTimedInput: false);
        }

        index = Math.Max(index, 0);
        At(index + 1) = Merge(At(index), At(index + 1));
        for (int i = index; i > 0; i--)
        {
            At(i) = At(i - 1);
        }

        _frames[_head] = default;
        _head = (_head + 1) % _frames.Length;
        _count--;
    }

    private int FindMergePair(bool keepTimedInput)
    {
        for (int i = 0; i < _count - 1; i++)
        {
            PlayerCommandFrame newer = At(i + 1);
            if (BufferedPlayerCommandInbox.HasSameMovement(At(i).MoveIntent, newer.MoveIntent)
                && !(keepTimedInput && BufferedPlayerCommandInbox.HasTimedInput(newer)))
            {
                return i;
            }
        }

        return -1;
    }

    private ref PlayerCommandFrame At(int index)
    {
        return ref _frames[(_head + index) % _frames.Length];
    }

    // Folds an older frame into a newer one: the newer frame's session, sequence, axes and facing
    // win (a missing facing or mode falls back to the older one), look deltas add up, and one-shot
    // inputs from either frame are kept.
    public static PlayerCommandFrame Merge(PlayerCommandFrame older, PlayerCommandFrame newer)
    {
        PlayerMoveIntent olderMove = older.MoveIntent;
        PlayerMoveIntent newerMove = newer.MoveIntent;
        PlayerMoveIntent move = new(
            newerMove.ForwardAxis,
            newerMove.StrafeAxis,
            newerMove.VerticalAxis,
            JumpPressed: olderMove.JumpPressed || newerMove.JumpPressed,
            FreezePressed: olderMove.FreezePressed || newerMove.FreezePressed,
            ToggleNoclipPressed: olderMove.ToggleNoclipPressed || newerMove.ToggleNoclipPressed);
        PlayerLookIntent look = new(
            older.LookIntent.YawDelta + newer.LookIntent.YawDelta,
            older.LookIntent.PitchDelta + newer.LookIntent.PitchDelta,
            newer.LookIntent.Facing ?? older.LookIntent.Facing);

        return new PlayerCommandFrame(
            newer.SessionId,
            newer.Sequence,
            new PlayerModeSwitchIntent(newer.RequestedMode.Mode ?? older.RequestedMode.Mode),
            move,
            look,
            older.PrimaryUsePressed || newer.PrimaryUsePressed);
    }
}
