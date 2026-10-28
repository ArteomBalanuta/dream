using Dream.Simulation.Players;
using Dream.Simulation.Runtime;

namespace Dream.Multiplayer.Sessions;

// Server-side jitter buffer: a ring of command frames per session, of which each tick pops exactly
// one. LatestPlayerCommandInbox kept only the newest frame, so a backlog that arrived after a
// client or network stall moved the seat one tick instead of the whole stall.
//
// Each slot stands for a number of ticks. A received frame stands for one; when the ring is full,
// the oldest two slots merge into one that stands for both (one-shot presses ORed, look deltas
// summed, newer axes, facing and sequence), so a 64-frame burst still replays 64 ticks of input
// through 8 slots. A slot that stands for several ticks is popped once per tick with a sequence
// counted back from its newest one, so the acknowledged sequence never runs ahead of what the
// seat has simulated; its look deltas apply on the first of those ticks only.
//
// While the ring is empty nothing is popped and the runtime repeats the last frame for up to
// WorldSessionRuntime.MaxCommandRepeatTicks, then idles (NETR-3). When frames arrive after such a
// gap, the repeated ticks have already simulated them: as many leading backlog ticks as were
// repeated are skipped while they match the repeated frame's axes and facing, so the seat neither
// overshoots nor loses one-shot presses (a skipped frame's presses carry into the next one).
//
// Latency stays bounded in two ways:
// - Elastic shed: once the pending depth has stayed above ShedDepthThreshold for ShedWindowTicks
//   ticks without draining on its own, the ring merges down to one frame.
// - Fast senders: a client that sends one frame per render (144 Hz) delivers two or more frames
//   between most ticks. Once that is true for FastSenderEnterTicks of the last 16 ticks, the
//   session coalesces its arrivals into one pending frame, as LatestPlayerCommandInbox did.
//
// Enqueue runs on receive threads and pops on the tick thread, all under one lock. Ring storage is
// allocated per session on its first frame and pooled across Remove, so steady-state enqueue and
// pop allocate nothing.
public sealed class BufferedPlayerCommandInbox : IPlayerCommandInbox
{
    public const int Capacity = 8;
    public const int ShedDepthThreshold = 3;
    public const int ShedWindowTicks = 32;
    public const int FastSenderEnterTicks = 8;
    public const int FastSenderExitTicks = 3;

    private readonly object _sync = new();
    private readonly Dictionary<PlayerSessionId, SessionRing> _rings = new();
    private readonly Stack<SessionRing> _pool = new();

    public bool HasPending
    {
        get
        {
            lock (_sync)
            {
                foreach (SessionRing ring in _rings.Values)
                {
                    if (ring.Count > 0)
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }

    public void Enqueue(PlayerCommandFrame commandFrame)
    {
        lock (_sync)
        {
            if (!_rings.TryGetValue(commandFrame.SessionId, out SessionRing? ring))
            {
                ring = _pool.Count > 0 ? _pool.Pop() : new SessionRing();
                _rings[commandFrame.SessionId] = ring;
            }

            ring.Enqueue(commandFrame);
        }
    }

    public IReadOnlyList<PlayerCommandFrame> Drain(IEnumerable<PlayerSessionId> sessionIds)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);

        List<PlayerCommandFrame> drained = [];
        DrainInto(sessionIds.ToArray(), drained);
        return drained;
    }

    // Replaces the destination's contents with one popped frame per given session that has one,
    // in session order. Called once per tick: a session without a pending frame counts a starved tick.
    public void DrainInto(IReadOnlyList<PlayerSessionId> sessionIds, List<PlayerCommandFrame> destination)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);
        ArgumentNullException.ThrowIfNull(destination);

        destination.Clear();
        lock (_sync)
        {
            if (_rings.Count == 0)
            {
                return;
            }

            for (int i = 0; i < sessionIds.Count; i++)
            {
                if (_rings.TryGetValue(sessionIds[i], out SessionRing? ring)
                    && ring.TryPop(out PlayerCommandFrame commandFrame))
                {
                    destination.Add(commandFrame);
                }
            }
        }
    }

    // Pops the session's frame for one tick.
    public bool TryPop(PlayerSessionId sessionId, out PlayerCommandFrame commandFrame)
    {
        lock (_sync)
        {
            if (_rings.TryGetValue(sessionId, out SessionRing? ring))
            {
                return ring.TryPop(out commandFrame);
            }

            commandFrame = default;
            return false;
        }
    }

    // Ticks of input pending for the session (a merged slot counts every tick it stands for).
    public int GetPendingTicks(PlayerSessionId sessionId)
    {
        lock (_sync)
        {
            return _rings.TryGetValue(sessionId, out SessionRing? ring) ? ring.PendingTicks : 0;
        }
    }

    public bool Contains(PlayerSessionId sessionId)
    {
        lock (_sync)
        {
            return _rings.ContainsKey(sessionId);
        }
    }

    // Drops the released session's frames and sequence state and returns its ring to the pool,
    // so a reconnect that reuses the session id starts from a fresh sequence.
    public void Remove(PlayerSessionId sessionId)
    {
        lock (_sync)
        {
            if (_rings.Remove(sessionId, out SessionRing? ring))
            {
                ring.Reset();
                _pool.Push(ring);
            }
        }
    }

    private struct Slot
    {
        public PlayerCommandFrame Frame;
        public int Ticks;
    }

    private sealed class SessionRing
    {
        private readonly Slot[] _slots = new Slot[Capacity];
        private int _head;
        private bool _hasEnqueued;
        private ulong _lastEnqueuedSequence;
        private bool _hasPopped;
        private PlayerCommandFrame _lastPopped;
        private int _starvedTicks;
        private int _arrivalsSincePop;
        private int _arrivalHistory;
        private bool _fastSender;
        private int _aboveThresholdTicks;
        private int _windowStartDepth;

        public int Count { get; private set; }

        public int PendingTicks { get; private set; }

        public void Reset()
        {
            Array.Clear(_slots);
            _head = 0;
            Count = 0;
            PendingTicks = 0;
            _hasEnqueued = false;
            _lastEnqueuedSequence = 0;
            _hasPopped = false;
            _lastPopped = default;
            _starvedTicks = 0;
            _arrivalsSincePop = 0;
            _arrivalHistory = 0;
            _fastSender = false;
            _aboveThresholdTicks = 0;
            _windowStartDepth = 0;
        }

        public void Enqueue(PlayerCommandFrame frame)
        {
            // Out-of-order and duplicate frames are stale.
            if (_hasEnqueued && frame.Sequence <= _lastEnqueuedSequence)
            {
                return;
            }

            _hasEnqueued = true;
            _lastEnqueuedSequence = frame.Sequence;
            _arrivalsSincePop++;

            if (_fastSender && Count > 0)
            {
                // One frame per render, not per tick: coalesce into the pending frame.
                ref Slot newest = ref SlotAt(Count - 1);
                newest.Frame = LatestPlayerCommandInbox.Merge(newest.Frame, frame);
                return;
            }

            if (Count == Capacity)
            {
                MergeOldestTwo();
            }

            ref Slot slot = ref SlotAt(Count);
            slot.Frame = frame;
            slot.Ticks = 1;
            Count++;
            PendingTicks++;
        }

        public bool TryPop(out PlayerCommandFrame frame)
        {
            TrackArrivalRate();

            if (Count == 0)
            {
                if (_hasPopped && _starvedTicks < int.MaxValue)
                {
                    _starvedTicks++;
                }

                _aboveThresholdTicks = 0;
                frame = default;
                return false;
            }

            SkipRepeatedTicks();

            ref Slot oldest = ref SlotAt(0);
            ulong sequence = oldest.Frame.Sequence - (ulong)(oldest.Ticks - 1);
            if (_hasPopped && sequence <= _lastPopped.Sequence)
            {
                sequence = _lastPopped.Sequence + 1;
            }

            frame = oldest.Frame with { Sequence = sequence };
            ConsumeOldestTick();

            _lastPopped = frame;
            _hasPopped = true;
            ShedIfLatencyStuck();
            return true;
        }

        // Ticks repeated by the runtime while the ring was empty already simulated the leading
        // backlog frames, as long as those match the repeated frame. The newest pending tick is
        // never skipped, so the seat still gets a fresh frame.
        private void SkipRepeatedTicks()
        {
            int debt = Math.Min(_starvedTicks, WorldSessionRuntime.MaxCommandRepeatTicks);
            _starvedTicks = 0;
            while (debt > 0 && PendingTicks > 1 && MatchesRepeated(SlotAt(0).Frame))
            {
                ref Slot oldest = ref SlotAt(0);
                if (oldest.Ticks > 1)
                {
                    oldest.Ticks--;
                    PendingTicks--;
                    // The skipped tick carried the look deltas; the next one must not apply them again.
                    oldest.Frame = WithoutLookDeltas(oldest.Frame);
                }
                else
                {
                    // Carry presses and look deltas into the next frame; it keeps its own sequence.
                    PlayerCommandFrame skipped = oldest.Frame;
                    RemoveOldest();
                    ref Slot next = ref SlotAt(0);
                    next.Frame = LatestPlayerCommandInbox.Merge(skipped, next.Frame);
                }

                debt--;
            }
        }

        private bool MatchesRepeated(PlayerCommandFrame frame)
        {
            PlayerMoveIntent move = frame.MoveIntent;
            PlayerMoveIntent repeated = _lastPopped.MoveIntent;
            return _hasPopped
                && move.ForwardAxis == repeated.ForwardAxis
                && move.StrafeAxis == repeated.StrafeAxis
                && move.VerticalAxis == repeated.VerticalAxis
                && frame.LookIntent.Facing == _lastPopped.LookIntent.Facing;
        }

        // A sender that delivers two or more frames between most ticks sends per render frame;
        // hysteresis keeps a jittery per-tick sender from flapping.
        private void TrackArrivalRate()
        {
            _arrivalHistory = ((_arrivalHistory << 1) | (_arrivalsSincePop >= 2 ? 1 : 0)) & 0xFFFF;
            _arrivalsSincePop = 0;
            int fastTicks = System.Numerics.BitOperations.PopCount((uint)_arrivalHistory);
            if (!_fastSender && fastTicks >= FastSenderEnterTicks)
            {
                _fastSender = true;
                MergeDownToOne();
            }
            else if (_fastSender && fastTicks <= FastSenderExitTicks)
            {
                _fastSender = false;
            }
        }

        // Elastic shed: a depth above the threshold that has not drained on its own over a whole
        // window is latency the sender keeps topping up, so cut it to one frame. A backlog that is
        // draining fast enough to reach the threshold within another window is left to replay.
        private void ShedIfLatencyStuck()
        {
            if (PendingTicks <= ShedDepthThreshold)
            {
                _aboveThresholdTicks = 0;
                return;
            }

            if (_aboveThresholdTicks == 0)
            {
                _windowStartDepth = PendingTicks;
            }

            _aboveThresholdTicks++;
            if (_aboveThresholdTicks < ShedWindowTicks)
            {
                return;
            }

            _aboveThresholdTicks = 0;
            int drained = _windowStartDepth - PendingTicks;
            if (drained < PendingTicks - ShedDepthThreshold)
            {
                MergeDownToOne();
            }
        }

        private void MergeDownToOne()
        {
            while (Count > 1)
            {
                MergeOldestTwo();
            }

            if (Count == 1)
            {
                SlotAt(0).Ticks = 1;
                PendingTicks = 1;
            }
        }

        private void MergeOldestTwo()
        {
            Slot oldest = SlotAt(0);
            RemoveOldest();
            ref Slot next = ref SlotAt(0);
            next.Frame = LatestPlayerCommandInbox.Merge(oldest.Frame, next.Frame);
            next.Ticks += oldest.Ticks;
            PendingTicks += oldest.Ticks;
        }

        private void ConsumeOldestTick()
        {
            ref Slot oldest = ref SlotAt(0);
            oldest.Ticks--;
            PendingTicks--;
            if (oldest.Ticks == 0)
            {
                RemoveOldest();
            }
            else
            {
                oldest.Frame = WithoutLookDeltas(oldest.Frame);
            }
        }

        // Removes the oldest slot along with the ticks it stood for.
        private void RemoveOldest()
        {
            PendingTicks -= _slots[_head].Ticks;
            _slots[_head] = default;
            _head = (_head + 1) % Capacity;
            Count--;
        }

        private ref Slot SlotAt(int index)
        {
            return ref _slots[(_head + index) % Capacity];
        }

        private static PlayerCommandFrame WithoutLookDeltas(PlayerCommandFrame frame)
        {
            return frame.LookIntent.YawDelta == 0f && frame.LookIntent.PitchDelta == 0f
                ? frame
                : frame with { LookIntent = new PlayerLookIntent(0f, 0f, frame.LookIntent.Facing) };
        }
    }
}
