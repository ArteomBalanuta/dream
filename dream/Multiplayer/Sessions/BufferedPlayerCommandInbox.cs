using Dream.Simulation.Players;
using Dream.Simulation.Runtime;

namespace Dream.Multiplayer.Sessions;

// Server-side jitter buffer: a ring of command frames per session, of which each tick pops exactly
// one. LatestPlayerCommandInbox kept only the newest frame, so a backlog that arrived after a
// client or network stall moved the seat one tick instead of the whole stall.
//
// Each slot stands for a number of ticks. A command's sequence is its client prediction tick
// (NETR-4), so a received frame stands for as many ticks as its sequence advanced past the last
// one received (at least one, at most MaxTicksPerFrame): a frame the client's outbound queue folded
// from several ticks during a send stall counts every one of them. When the ring is full, two
// neighbouring slots merge into one that stands for both (one-shot presses ORed, look deltas
// summed, newer axes, facing and sequence; at most MaxMergedSlotTicks). The merged slot is popped
// from the older slot's first tick, so its presses land there: the oldest neighbours with the same
// movement axes whose newer slot has no unapplied press or mode request merge first, which keeps
// every run's ticks exact (walk, then idle) and every press at its own tick. Only when no such
// pair exists do the oldest neighbours with the same axes merge (a press then moves earlier, to the
// start of the merged slot), and with none of those either, the oldest two. A slot that stands for
// several ticks is popped once per tick with a sequence counted back from its newest one; its look
// deltas apply on the first tick only.
//
// While the ring is empty nothing is popped and the runtime repeats the last frame for
// WorldSessionRuntime.MaxCommandRepeatTicks ticks, then idles (NETR-3). Those starved ticks are
// skip debt: they already simulated the commands that arrive next. When frames arrive, leading
// backlog ticks are skipped while they match what the runtime simulated in their place: the
// repeated frame's movement axes for the first MaxCommandRepeatTicks starved ticks, no movement
// for the rest (a client whose send is stalled predicts and sends idle past the repeat window, see
// ClientCommandTickStream). The match is exact unless the backlog shows a client-side stall (a
// frame whose sequence jumped, i.e. one the client's outbound queue folded): then a client switch
// to idle within SkipBoundaryToleranceTicks of the server's is accepted. A pure network delay
// leaves the client's commands unfolded, and an inexact switch there would skip real movement.
// Facing is absolute and applies on the next popped frame, so it is not compared. The newest
// pending tick is never skipped, so the seat still gets a fresh frame; a frame with a one-shot
// press (jump, freeze, noclip toggle) is not skipped either but popped at its own tick, so the
// press lands where the client predicted it. A skipped frame's primary use and look deltas carry
// into the next one. Debt that a short backlog could not spend is kept for the next frames (up to
// MaxSkipDebtTicks) and cleared as soon as the frame about to be popped does not match, or once
// Capacity frames have popped without a starved tick in between: frames arriving one per tick
// never let a skip run, and stale debt would later skip real ticks.
//
// The acknowledged sequence (the popped frame's) never runs ahead of the commands the seat has
// simulated or skipped as already simulated, with two exceptions that drop ticks outright and
// advance the ack past them: once a skip has spent the idle debt, idle commands still above
// ShedDepthThreshold (the latency the ring held before the stall) are dropped by TrimIdleStallTail
// (they cost no movement, whereas the shed would later fold the walking ticks queued behind them),
// and the shed's MergeDownToOne folds every pending tick into one.
//
// Latency stays bounded in two ways:
// - Elastic shed: once the pending depth has stayed above ShedDepthThreshold for ShedWindowTicks
//   ticks without draining on its own, the ring merges down to one frame.
// - Fast senders: a legacy client that numbers one frame per render (144 Hz) instead of per tick
//   pushes its newest sequence ahead of the server's tick count, tick after tick. A per-tick
//   client (any frame rate, NETR-4) cannot: its sequence is a 64 Hz clock, so however its frames
//   bunch up (pairs at 30 fps, a burst after a stall), the newest sequence minus the ticks popped
//   never climbs past its earlier maximum except when its delivery delay drops. Once that lead rose
//   on FastSenderEnterTicks of the last 16 ticks, the session coalesces its arrivals into one
//   pending frame, as LatestPlayerCommandInbox did; it leaves that mode once the lead rose on at
//   most FastSenderExitTicks of the last 16. A server ticking below 64 Hz (overloaded) sees an
//   honest per-tick client's lead rise too; such entries, whose sequence advanced no faster than
//   the wall clock's 64 Hz, are counted in HonestSenderFastModeEntries.
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
    // The most ticks one received frame stands for, and the most skip debt a session keeps: 4 s.
    public const int MaxTicksPerFrame = 4 * SimulationClock.AuthoritativeTicksPerSecond;
    public const int MaxSkipDebtTicks = 4 * SimulationClock.AuthoritativeTicksPerSecond;
    // Starved ticks the runtime repeats the last command before idling (NETR-3), for clients,
    // which do not reference the authoritative runtime.
    public const int ServerRepeatTicks = WorldSessionRuntime.MaxCommandRepeatTicks;
    // How far the client's repeat-to-idle switch may sit from the server's and still be skipped.
    public const int SkipBoundaryToleranceTicks = 4;
    // The most ticks a slot merged on overflow stands for.
    public const int MaxMergedSlotTicks = MaxTicksPerFrame * Capacity;
    // A fast-sender entry counts as an honest per-tick client's when its sequence advanced by at
    // most this many ticks per 64 Hz wall-clock tick (plus HonestSenderSlackTicks): a per-render
    // sender at 144 Hz advances 2.25.
    private const double HonestSenderRateLimit = 1.25d;
    private const double HonestSenderSlackTicks = 2d;
    private const int SendRateHistoryTicks = 16;

    private readonly object _sync = new();
    private readonly Dictionary<PlayerSessionId, SessionRing> _rings = new();
    private readonly Stack<SessionRing> _pool = new();
    private readonly Func<long> _timestamp;
    private readonly long _timestampFrequency;
    private long _fastSenderEntries;
    private long _honestSenderFastModeEntries;

    public BufferedPlayerCommandInbox()
        : this(System.Diagnostics.Stopwatch.GetTimestamp, System.Diagnostics.Stopwatch.Frequency)
    {
    }

    // The timestamp source (timestampFrequency units per second) only classifies fast-sender
    // entries for HonestSenderFastModeEntries; tests pass a manual clock.
    public BufferedPlayerCommandInbox(Func<long> timestamp, long timestampFrequency)
    {
        ArgumentNullException.ThrowIfNull(timestamp);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timestampFrequency, 0L);
        _timestamp = timestamp;
        _timestampFrequency = timestampFrequency;
    }

    // Sessions switched into fast-sender (coalescing) mode, over the inbox's lifetime.
    public long FastSenderEntries
    {
        get
        {
            lock (_sync)
            {
                return _fastSenderEntries;
            }
        }
    }

    // Fast-sender entries of sessions whose sequence advanced no faster than the wall clock's
    // 64 Hz: honest per-tick clients flipped because the server ticked below 64 Hz (overload).
    // Their frames are then coalesced, as a legacy per-render client's would be. Not yet wired
    // into server metrics.
    public long HonestSenderFastModeEntries
    {
        get
        {
            lock (_sync)
            {
                return _honestSenderFastModeEntries;
            }
        }
    }

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
                ring = _pool.Count > 0 ? _pool.Pop() : new SessionRing(this);
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

    // Starved ticks the session's next frames may still skip.
    public int GetSkipDebtTicks(PlayerSessionId sessionId)
    {
        lock (_sync)
        {
            return _rings.TryGetValue(sessionId, out SessionRing? ring) ? ring.SkipDebt : 0;
        }
    }

    // Whether the session is treated as a render-rate sender whose arrivals are coalesced.
    public bool IsFastSender(PlayerSessionId sessionId)
    {
        lock (_sync)
        {
            return _rings.TryGetValue(sessionId, out SessionRing? ring) && ring.IsFastSender;
        }
    }

    public bool Contains(PlayerSessionId sessionId)
    {
        lock (_sync)
        {
            return _rings.ContainsKey(sessionId);
        }
    }

    // Drops the released session's frames, sequence, skip debt and send-rate state and returns its
    // ring to the pool, so a reconnect that reuses the session id starts from a fresh sequence
    // (including after a stale frame from the old connection recreated the ring).
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

    // Same movement axes; facing, presses, look deltas and primary use are not compared.
    public static bool HasSameMovement(PlayerMoveIntent first, PlayerMoveIntent second)
    {
        return first.ForwardAxis == second.ForwardAxis
            && first.StrafeAxis == second.StrafeAxis
            && first.VerticalAxis == second.VerticalAxis;
    }

    // Jump, freeze or noclip toggle: presses a tick consumes once, whose timing moves the player.
    // A mode request is idempotent (some clients repeat it every frame), so it carries instead.
    public static bool HasOneShotInput(PlayerCommandFrame frame)
    {
        PlayerMoveIntent move = frame.MoveIntent;
        return move.JumpPressed || move.FreezePressed || move.ToggleNoclipPressed;
    }

    // A one-shot press or a mode request: input whose tick an overflow merge must not move earlier
    // by folding its frame into an older one.
    public static bool HasTimedInput(PlayerCommandFrame frame)
    {
        return HasOneShotInput(frame) || frame.RequestedMode.Mode.HasValue;
    }

    private struct Slot
    {
        public PlayerCommandFrame Frame;
        public int Ticks;
        // A tick of this slot was popped or skipped, so its presses have been applied.
        public bool Started;
    }

    private sealed class SessionRing(BufferedPlayerCommandInbox owner)
    {
        private readonly Slot[] _slots = new Slot[Capacity];
        private readonly long[] _rateTimestamps = new long[SendRateHistoryTicks];
        private readonly ulong[] _rateSequences = new ulong[SendRateHistoryTicks];
        private int _head;
        private bool _hasEnqueued;
        private ulong _lastEnqueuedSequence;
        private bool _hasPopped;
        private PlayerCommandFrame _lastPopped;
        // Consecutive starved ticks since the last pop, counted like the runtime's NETR-3 window.
        private int _starvedTicks;
        private int _repeatDebt;
        private int _idleDebt;
        private PlayerMoveIntent _repeatedMove;
        // A frame whose sequence jumped (the client folded ticks during a stall) arrived since the
        // current skip debt started: the client-side stall that makes the boundary tolerance apply.
        private bool _sawSequenceJump;
        private long _rateSamples;
        // Frames popped since the last starved tick while skip debt was pending.
        private int _popsWithoutStarve;
        private ulong _ticks;
        private bool _hasLead;
        private long _maxLead;
        private int _leadRiseHistory;
        private bool _fastSender;
        private int _aboveThresholdTicks;
        private int _windowStartDepth;

        public int Count { get; private set; }

        public int PendingTicks { get; private set; }

        public int SkipDebt => _repeatDebt + _idleDebt;

        public bool IsFastSender => _fastSender;

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
            _repeatDebt = 0;
            _idleDebt = 0;
            _repeatedMove = default;
            _sawSequenceJump = false;
            _popsWithoutStarve = 0;
            Array.Clear(_rateTimestamps);
            _rateSamples = 0;
            Array.Clear(_rateSequences);
            _ticks = 0;
            _hasLead = false;
            _maxLead = 0;
            _leadRiseHistory = 0;
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

            // The sequence is the client's tick: a jump is a frame folded from several ticks.
            int ticks = _hasEnqueued
                ? (int)Math.Min(frame.Sequence - _lastEnqueuedSequence, (ulong)MaxTicksPerFrame)
                : 1;
            _hasEnqueued = true;
            _lastEnqueuedSequence = frame.Sequence;
            if (ticks > 1)
            {
                _sawSequenceJump = true;
            }

            if (_fastSender && Count > 0)
            {
                // One frame per render, not per tick: coalesce into the pending frame.
                ref Slot newest = ref SlotAt(Count - 1);
                newest.Frame = LatestPlayerCommandInbox.Merge(newest.Frame, frame);
                return;
            }

            if (Count == Capacity)
            {
                MergeOldestMatchingNeighbours();
            }

            ref Slot slot = ref SlotAt(Count);
            slot.Frame = frame;
            slot.Ticks = ticks;
            slot.Started = false;
            Count++;
            PendingTicks += ticks;
        }

        public bool TryPop(out PlayerCommandFrame frame)
        {
            TrackSendRate();

            if (Count == 0)
            {
                if (_hasPopped)
                {
                    CountStarvedTick();
                }

                _aboveThresholdTicks = 0;
                frame = default;
                return false;
            }

            SkipSimulatedTicks();
            _starvedTicks = 0;
            if (SkipDebt > 0 && !MatchesSimulatedTick(SlotAt(0).Frame.MoveIntent))
            {
                // The frame about to be popped is not what the starved ticks simulated, so the
                // debt is stale: a later frame matching it by chance must not be skipped.
                ClearSkipDebt();
            }

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
            if (SkipDebt > 0 && ++_popsWithoutStarve >= Capacity)
            {
                // A ring's worth of frames popped without the session starving again: the backlog
                // the debt was waiting for has come and gone.
                ClearSkipDebt();
            }

            ShedIfLatencyStuck();
            return true;
        }

        // The runtime simulates a starved tick with the last popped frame for the first
        // MaxCommandRepeatTicks of a starved run, then with no movement (NETR-3).
        private void CountStarvedTick()
        {
            if (SkipDebt == 0)
            {
                // A new stall: only a sequence jump that arrives after it shows a client stall.
                _sawSequenceJump = false;
            }

            _popsWithoutStarve = 0;
            if (_starvedTicks < int.MaxValue)
            {
                _starvedTicks++;
            }

            if (_starvedTicks <= WorldSessionRuntime.MaxCommandRepeatTicks)
            {
                if (_repeatDebt > 0 && !HasSameMovement(_repeatedMove, _lastPopped.MoveIntent))
                {
                    // Debt kept from an earlier run repeated other axes; its frames will not match.
                    _repeatDebt = 0;
                }

                _repeatedMove = _lastPopped.MoveIntent;
                if (SkipDebt < MaxSkipDebtTicks)
                {
                    _repeatDebt++;
                }
            }
            else if (SkipDebt < MaxSkipDebtTicks)
            {
                _idleDebt++;
            }
        }

        // Starved ticks already simulated the leading backlog ticks, as long as those match what
        // the runtime simulated in their place: the repeated axes first, then no movement.
        private void SkipSimulatedTicks()
        {
            bool spentIdleDebt = false;
            while (SkipDebt > 0 && PendingTicks > 1)
            {
                ref Slot oldest = ref SlotAt(0);
                if (!MatchesSimulatedTick(oldest.Frame.MoveIntent))
                {
                    ClearSkipDebt();
                    return;
                }

                if (!oldest.Started && HasOneShotInput(oldest.Frame))
                {
                    // Pop the press at its own tick; the debt waits for the frames after it.
                    return;
                }

                SkipOldestTick();
                if (_repeatDebt > 0)
                {
                    _repeatDebt--;
                }
                else
                {
                    _idleDebt--;
                    spentIdleDebt = true;
                }
            }

            if (spentIdleDebt && SkipDebt == 0)
            {
                TrimIdleStallTail();
            }
        }

        private void ClearSkipDebt()
        {
            _repeatDebt = 0;
            _idleDebt = 0;
            _sawSequenceJump = false;
            _popsWithoutStarve = 0;
        }

        // The stall's idle commands outnumber the server's idle ticks by the latency the ring held
        // before the stall. Left in, they keep the depth above the threshold while the client sends
        // one command per tick, until the shed merges the backlog down to one tick and loses the
        // walking ticks queued behind them. Dropping them now costs no movement.
        private void TrimIdleStallTail()
        {
            while (PendingTicks > ShedDepthThreshold)
            {
                ref Slot oldest = ref SlotAt(0);
                if (!HasSameMovement(oldest.Frame.MoveIntent, PlayerMoveIntent.None)
                    || (!oldest.Started && HasOneShotInput(oldest.Frame)))
                {
                    return;
                }

                SkipOldestTick();
            }
        }

        private void SkipOldestTick()
        {
            ref Slot oldest = ref SlotAt(0);
            if (oldest.Ticks > 1)
            {
                oldest.Ticks--;
                oldest.Started = true;
                PendingTicks--;
                // The skipped tick carried the look deltas; the next one must not apply them again.
                oldest.Frame = WithoutLookDeltas(oldest.Frame);
            }
            else
            {
                // Carry primary use and look deltas into the next frame; it keeps its own sequence.
                PlayerCommandFrame skipped = oldest.Frame;
                RemoveOldest();
                ref Slot next = ref SlotAt(0);
                next.Frame = LatestPlayerCommandInbox.Merge(skipped, next.Frame);
            }
        }

        // Whether the oldest pending tick may stand in for the next starved tick. A client whose
        // send loop stalled switches from the repeated movement to idle where it estimates the
        // server did, which can be off by a few ticks (send timing, a frame already in flight):
        // when the backlog shows that stall (a sequence jump), a switch within
        // SkipBoundaryToleranceTicks of the server's is accepted (those few ticks diverge, and the
        // reconciler smooths them out) instead of throwing away the whole debt and replaying the
        // stall's idle backlog. Without that evidence the starvation was the network's: the client
        // sent what it predicted, so only an exact match is skipped, and a mismatch replays the
        // backlog (its real movement included) instead of dropping walk ticks.
        private bool MatchesSimulatedTick(PlayerMoveIntent move)
        {
            bool idle = HasSameMovement(move, PlayerMoveIntent.None);
            if (_repeatDebt > 0)
            {
                // The client went idle a little before the server's repeat window ended.
                return HasSameMovement(move, _repeatedMove)
                    || (_sawSequenceJump && idle && _idleDebt > 0 && _repeatDebt <= SkipBoundaryToleranceTicks);
            }

            // The client kept the repeated movement a little past the server's repeat window.
            return idle
                || (_sawSequenceJump && HasSameMovement(move, _repeatedMove) && RepeatedRunEndsInIdleWithinTolerance());
        }

        // Pending ticks with the repeated movement, from the oldest, end within the tolerance at
        // a pending idle tick.
        private bool RepeatedRunEndsInIdleWithinTolerance()
        {
            int run = 0;
            for (int i = 0; i < Count; i++)
            {
                PlayerMoveIntent move = SlotAt(i).Frame.MoveIntent;
                if (HasSameMovement(move, PlayerMoveIntent.None))
                {
                    return run > 0;
                }

                if (!HasSameMovement(move, _repeatedMove))
                {
                    return false;
                }

                run += SlotAt(i).Ticks;
                if (run > SkipBoundaryToleranceTicks)
                {
                    return false;
                }
            }

            return false;
        }

        // A per-tick sender's newest sequence minus the ticks popped (its lead) only climbs past
        // its earlier maximum when its delivery delay drops; a per-render sender's climbs on most
        // ticks. Hysteresis keeps the mode from flapping.
        private void TrackSendRate()
        {
            _ticks++;
            long now = owner._timestamp();
            // The oldest (wall time, newest sequence) sample of the history: SendRateHistoryTicks
            // ticks ago, or the first one taken after the first frame arrived.
            long thenTimestamp = now;
            ulong thenSequence = _lastEnqueuedSequence;
            if (_hasEnqueued)
            {
                _rateSamples++;
                int sample = (int)(_rateSamples % SendRateHistoryTicks);
                if (_rateSamples > 1)
                {
                    int oldest = _rateSamples > SendRateHistoryTicks ? sample : 1;
                    thenTimestamp = _rateTimestamps[oldest];
                    thenSequence = _rateSequences[oldest];
                }

                _rateTimestamps[sample] = now;
                _rateSequences[sample] = _lastEnqueuedSequence;
            }

            bool rose = false;
            if (_hasEnqueued)
            {
                long lead = unchecked((long)_lastEnqueuedSequence - (long)_ticks);
                if (!_hasLead)
                {
                    _hasLead = true;
                    _maxLead = lead;
                }
                else if (lead > _maxLead)
                {
                    rose = true;
                    _maxLead = lead;
                }
            }

            _leadRiseHistory = ((_leadRiseHistory << 1) | (rose ? 1 : 0)) & 0xFFFF;
            int risingTicks = System.Numerics.BitOperations.PopCount((uint)_leadRiseHistory);
            if (!_fastSender && risingTicks >= FastSenderEnterTicks)
            {
                _fastSender = true;
                owner._fastSenderEntries++;
                if (IsWallClockRate(now - thenTimestamp, _lastEnqueuedSequence - thenSequence))
                {
                    owner._honestSenderFastModeEntries++;
                }

                MergeDownToOne();
            }
            else if (_fastSender && risingTicks <= FastSenderExitTicks)
            {
                _fastSender = false;
            }
        }

        // A sequence that advanced no faster than 64 Hz of wall-clock time over the history: an
        // honest per-tick client, whose lead rose because the server ticked slower.
        private bool IsWallClockRate(long elapsedTimestamp, ulong sequenceAdvance)
        {
            double wallTicks = (double)elapsedTimestamp * SimulationClock.AuthoritativeTicksPerSecond
                / owner._timestampFrequency;
            return sequenceAdvance <= (wallTicks * HonestSenderRateLimit) + HonestSenderSlackTicks;
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
                MergeIntoNext(0);
            }

            if (Count == 1)
            {
                SlotAt(0).Ticks = 1;
                PendingTicks = 1;
            }
        }

        // Overflow: merge the oldest neighbours that move the same way and whose newer slot has no
        // unapplied press or mode request, so the merged slot's ticks all simulate what the client
        // predicted and its presses stay at their own tick (a merged slot pops from the older
        // slot's first tick). With no such pair, the oldest neighbours that move the same way;
        // with none of those, the oldest two.
        private void MergeOldestMatchingNeighbours()
        {
            int index = FindMergePair(keepTimedInput: true);
            if (index < 0)
            {
                index = FindMergePair(keepTimedInput: false);
            }

            MergeIntoNext(Math.Max(index, 0));
        }

        private int FindMergePair(bool keepTimedInput)
        {
            for (int i = 0; i < Count - 1; i++)
            {
                ref Slot newer = ref SlotAt(i + 1);
                if (HasSameMovement(SlotAt(i).Frame.MoveIntent, newer.Frame.MoveIntent)
                    && !(keepTimedInput && !newer.Started && HasTimedInput(newer.Frame)))
                {
                    return i;
                }
            }

            return -1;
        }

        // Folds the slot at the index into the next one, which then stands for both.
        private void MergeIntoNext(int index)
        {
            Slot older = SlotAt(index);
            ref Slot next = ref SlotAt(index + 1);
            next.Frame = LatestPlayerCommandInbox.Merge(older.Frame, next.Frame);
            int ticks = next.Ticks + older.Ticks;
            if (ticks > MaxMergedSlotTicks)
            {
                // The ticks past the cap are dropped, and the ack advances past them.
                PendingTicks -= ticks - MaxMergedSlotTicks;
                ticks = MaxMergedSlotTicks;
            }

            next.Ticks = ticks;
            next.Started = older.Started && next.Started;
            for (int i = index; i > 0; i--)
            {
                SlotAt(i) = SlotAt(i - 1);
            }

            _slots[_head] = default;
            _head = (_head + 1) % Capacity;
            Count--;
        }

        private void ConsumeOldestTick()
        {
            ref Slot oldest = ref SlotAt(0);
            oldest.Ticks--;
            oldest.Started = true;
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
