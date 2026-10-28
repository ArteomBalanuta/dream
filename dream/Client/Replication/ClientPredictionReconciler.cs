using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;
using OpenTK.Mathematics;

namespace Dream.Client.Replication;

public enum ClientReconciliationKind
{
    // Server agrees with what the client predicted for the acknowledged command.
    None,
    // Small divergence: blended into the local position over a few frames.
    Smooth,
    // Large divergence (teleport, respawn, rejected move) or no usable history: snap.
    Snap
}

// SnapPosition is where a Snap puts the player: the server position with the unacknowledged
// commands replayed on top, or just the server position when nothing was acknowledged or the
// replay was discarded. Error is the server position minus the prediction for the acknowledged
// command.

public readonly record struct ClientReconciliationResult(
    ClientReconciliationKind Kind,
    Vector3 Error,
    Vector3 SnapPosition);

// Reconciles the locally predicted player with authoritative snapshots.
//
// The client records where its prediction put the player after each local tick, keyed by the
// command sequence that tick simulated. A snapshot carries the server position and the last
// command sequence the server accepted. Comparing the server position with the prediction for
// that same command isolates real divergence from the normal latency gap. Comparing with the
// current position, as the old hard snap effectively did, pulled the player back by
// speed × round-trip on every snapshot.
//
// The client records exactly one sample per prediction tick, and its command sequence is that tick
// (NETR-4), so the samples after the acknowledged one are the unacknowledged commands, one per
// tick. A correction replays them from the server state: every later sample, and so the player,
// moves by the error, which is what re-simulating those ticks from the server position gives as
// long as the error is a displacement (the replay is the per-tick prediction deltas, not a second
// physics run: stepping the client's physics world again would also re-step every rubble body).
//
// Small errors are applied as a pending offset that drains over SmoothingSeconds. Large ones snap:
// the player moves at once to the server position plus the replayed ticks, its velocity is zeroed
// by the applier, and the shifted history stays, so later snapshots compare against the replayed
// path instead of finding no comparable sample.
//
// The replay is a translation of the client's own path, so it is only trusted for moderate
// errors. At ReplayDiscardDistance or more (a teleport, a respawn, a long rejected walk), or when
// the translated path would put the player inside a solid cell of the client's map, the snap lands
// on the server position alone and the unacknowledged samples are rebased there: replaying a walk
// that the server stopped (at an obstacle the client walked through) would put the player into or
// past the obstacle again, and every later snapshot would pull it back.
//
// Starved snapshots (W9-2): a snapshot whose StarvedTicks is k holds the server's position k ticks
// after the ack, simulated with the acknowledged command repeated (then idle past
// BufferedPlayerCommandInbox.ServerRepeatTicks), not with the client's own commands for those
// ticks. The client's sample k ticks after the ack stands for that state only when its commands
// for those ticks are what the server simulated: the ack's movement axes (or idle once the client
// stopped: idling earlier than the server only diverges for good, which a correction now fixes as
// well as one later), with no one-shot press. Each sample records its command's movement for that
// check. Otherwise (the player started walking, turned or jumped during the starve) the server
// will replay those commands after its starved ticks, so the comparison is with what the server
// did: the samples while the commands still matched, then the ack's step repeated. Comparing with
// the client's sample there pulled the player back and the replay pushed it forward again.
//
// StarvedTicks also counts the repeat debt the server's inbox still holds after a backlog
// started arriving (repeated ticks simulated ahead of the ack that it will skip later, see
// WorldSessionRuntime's CaptureStarvedTicks; idle debt is left out, as it did not move the player
// across the floor). Known limits: debt the inbox drops unspent (a ring's worth of pops without a
// starve) was a real lead, corrected once when it stops being reported; idle debt's vertical
// movement (a fall) is not counted. The extrapolated step is the ack
// tick's displacement (or the newest matching sample's), with gravity applied to its vertical part
// per tick while the player is airborne: it does not see a landing, a wall, or a jump the server
// had not simulated, and it is zero when the sample before the ack is missing. The motor stops
// horizontal movement at once on an idle tick, so the idle part needs no deceleration.
public sealed class ClientPredictionReconciler
{
    public const float DefaultDeadbandDistance = 0.05f;
    public const float DefaultSnapDistance = 3f;
    public const float DefaultSmoothingSeconds = 0.1f;
    public const float DefaultReplayDiscardDistance = 2f * DefaultSnapDistance;
    private const int MaxHistory = 256;
    // The player body's gravity (the physics world's, PhysicsIntegrator; PlayerMotor restores
    // 9.81 after a freeze or noclip toggle), as the change of a tick's vertical displacement.
    private const float GravityAcceleration = 9.8f;
    private const float GravityStepPerTick =
        GravityAcceleration * SimulationClock.FixedDeltaSeconds * SimulationClock.FixedDeltaSeconds;

    // One prediction tick: its command sequence, the intended position after it, and the movement
    // of the command it simulated (HasMove false when the caller did not give it, in which case the
    // sample is assumed to match the server's repeated command, as before W9 fix loop 2).
    private readonly record struct Sample(ulong Sequence, Vector3 Position, PlayerMoveIntent Move, bool HasMove)
    {
        public Sample WithPosition(Vector3 position) => this with { Position = position };
    }

    private readonly List<Sample> _history = [];
    private readonly float _deadbandDistance;
    private readonly float _snapDistance;
    private readonly float _smoothingSeconds;
    private readonly float _replayDiscardDistance;
    private Vector3 _pendingOffset;
    private float _smoothingRemainingSeconds;
    // The ack the last comparison used. A snapshot that repeats it comes from a server that has
    // simulated more ticks without a new command from this client (it repeats the last one, then
    // idles: NETR-3), so its position is the server's extrapolation, not the result of that
    // command; comparing it with the acknowledged prediction pulled the player along with every
    // starved snapshot, and the next real ack then counted the same ticks again.
    private ulong? _lastComparedSequence;

    public ClientPredictionReconciler(
        float deadbandDistance = DefaultDeadbandDistance,
        float snapDistance = DefaultSnapDistance,
        float smoothingSeconds = DefaultSmoothingSeconds,
        float replayDiscardDistance = DefaultReplayDiscardDistance)
    {
        _deadbandDistance = deadbandDistance;
        _snapDistance = snapDistance;
        _smoothingSeconds = smoothingSeconds;
        _replayDiscardDistance = Math.Max(replayDiscardDistance, snapDistance);
    }

    // Snaps that landed on the server position alone instead of the replayed path.
    public long DiscardedReplays { get; private set; }

    public Vector3 PendingOffset => _pendingOffset;

    public int HistoryCount => _history.Count;

    // Call after each local simulation tick with the command sequence that tick used and the
    // player's simulated position. Without the command's movement, the sample is assumed to match
    // what a starved server repeats.
    public void RecordPrediction(ulong commandSequence, Vector3 simulatedPosition)
    {
        Record(new Sample(commandSequence, simulatedPosition, default, HasMove: false));
    }

    // With the movement of the command the tick simulated (as sent to the server), so a starved
    // snapshot is only compared with this sample when the server simulated the same movement.
    public void RecordPrediction(ulong commandSequence, Vector3 simulatedPosition, PlayerMoveIntent move)
    {
        Record(new Sample(commandSequence, simulatedPosition, move, HasMove: true));
    }

    private void Record(Sample sample)
    {
        // Record where the player will be once pending smoothing is applied, so samples taken
        // mid-blend compare against the corrected path.
        sample = sample.WithPosition(sample.Position + _pendingOffset);
        ulong commandSequence = sample.Sequence;
        if (_history.Count > 0 && _history[^1].Sequence == commandSequence)
        {
            _history[^1] = sample;
            return;
        }

        if (_history.Count > 0 && commandSequence < _history[^1].Sequence)
        {
            return;
        }

        _history.Add(sample);
        if (_history.Count > MaxHistory)
        {
            _history.RemoveAt(0);
        }
    }

    // The map, when given, is the client's: a replayed path that enters one of its solid cells is
    // discarded. starvedTicks is the snapshot's ActorSnapshot.StarvedTicks: the server simulated
    // that many ticks after the acknowledged command without a newer one, so the position is
    // compared with the prediction that many ticks after the ack (W9-2). No allocation.
    public ClientReconciliationResult Reconcile(
        ulong? acknowledgedSequence,
        Vector3 serverPosition,
        Map? map = null,
        int starvedTicks = 0)
    {
        // No accepted command means the server is not moving this player from our input.
        if (!acknowledgedSequence.HasValue)
        {
            return Snap(serverPosition);
        }

        if (_lastComparedSequence == acknowledgedSequence.Value)
        {
            return new ClientReconciliationResult(ClientReconciliationKind.None, Vector3.Zero, serverPosition);
        }

        // Every recorded sample is newer than the ack (for example right after a snap, while the
        // server still lags behind): nothing to compare yet, so wait for a later snapshot.
        int sampleIndex = FindLatestSampleAtOrBefore(acknowledgedSequence.Value);
        if (sampleIndex < 0)
        {
            return new ClientReconciliationResult(ClientReconciliationKind.None, Vector3.Zero, serverPosition);
        }

        Vector3 predicted = PredictAfterStarvedTicks(sampleIndex, acknowledgedSequence.Value, starvedTicks);
        // Keep the acknowledged sample: the next comparison may need it as the latest at or
        // before its ack.
        _history.RemoveRange(0, sampleIndex);
        _lastComparedSequence = acknowledgedSequence.Value;

        Vector3 error = serverPosition - predicted;
        float distance = error.Length;
        if (distance <= _deadbandDistance)
        {
            return new ClientReconciliationResult(ClientReconciliationKind.None, error, serverPosition);
        }

        ShiftHistory(error);
        if (distance >= _snapDistance)
        {
            // The samples hold intended positions (pending smoothing included), so the newest one,
            // shifted, is the server state with every unacknowledged tick replayed on top; the
            // snap lands there and replaces whatever smoothing was still pending.
            _pendingOffset = Vector3.Zero;
            _smoothingRemainingSeconds = 0f;
            if (distance >= _replayDiscardDistance || ReplayEntersSolidCell(map))
            {
                // The ticks after the ack will be simulated from the server position; until their
                // acks say where they went, assume they did not move.
                for (int index = 1; index < _history.Count; index++)
                {
                    _history[index] = _history[index].WithPosition(serverPosition);
                }

                DiscardedReplays++;
                return new ClientReconciliationResult(ClientReconciliationKind.Snap, error, serverPosition);
            }

            return new ClientReconciliationResult(ClientReconciliationKind.Snap, error, _history[^1].Position);
        }

        _pendingOffset += error;
        _smoothingRemainingSeconds = _smoothingSeconds;
        return new ClientReconciliationResult(ClientReconciliationKind.Smooth, error, serverPosition);
    }

    // Returns how far to move the player this frame to drain the pending correction.
    public Vector3 TakeSmoothingStep(float deltaSeconds)
    {
        if (_pendingOffset == Vector3.Zero)
        {
            return Vector3.Zero;
        }

        // Linear blend that finishes exactly when the smoothing window ends; an exponential blend
        // would never fully converge.
        float fraction = _smoothingRemainingSeconds <= deltaSeconds
            ? 1f
            : Math.Clamp(deltaSeconds / _smoothingRemainingSeconds, 0f, 1f);
        _smoothingRemainingSeconds = Math.Max(0f, _smoothingRemainingSeconds - deltaSeconds);
        Vector3 step = fraction >= 1f ? _pendingOffset : _pendingOffset * fraction;
        _pendingOffset -= step;
        return step;
    }

    public void Reset()
    {
        _history.Clear();
        _pendingOffset = Vector3.Zero;
        _smoothingRemainingSeconds = 0f;
        _lastComparedSequence = null;
    }

    private void ShiftHistory(Vector3 error)
    {
        for (int index = 0; index < _history.Count; index++)
        {
            _history[index] = _history[index].WithPosition(_history[index].Position + error);
        }
    }

    // Whether a replayed (already shifted) unacknowledged sample lies in a solid cell of the map.
    private bool ReplayEntersSolidCell(Map? map)
    {
        if (map == null)
        {
            return false;
        }

        for (int index = 1; index < _history.Count; index++)
        {
            Vector3 position = _history[index].Position;
            Cube? cube = map.TryGetCubeAtCell(
                (int)MathF.Floor(position.X),
                (int)MathF.Floor(position.Y),
                (int)MathF.Floor(position.Z));
            if (cube is { IsSolid: true })
            {
                return true;
            }
        }

        return false;
    }

    private ClientReconciliationResult Snap(Vector3 serverPosition)
    {
        Reset();
        return new ClientReconciliationResult(ClientReconciliationKind.Snap, Vector3.Zero, serverPosition);
    }

    // The prediction to compare a snapshot with: the acknowledged sample, or with starved ticks what
    // the server simulated in them, the acknowledged command repeated (then idle,
    // BufferedPlayerCommandInbox.ServerRepeatTicks). The client's sample that many ticks later is
    // that state when its commands for those ticks match (see the class comment). Otherwise, and
    // for ticks the client has not predicted yet, the newest sample whose commands still exactly
    // match is extrapolated with its tick's displacement (its vertical part only once the repeat
    // window has passed and the server idles), gravity applied while airborne. No allocation.
    private Vector3 PredictAfterStarvedTicks(int acknowledgedIndex, ulong acknowledgedSequence, int starvedTicks)
    {
        Sample acknowledged = _history[acknowledgedIndex];
        if (starvedTicks <= 0)
        {
            return acknowledged.Position;
        }

        ulong target = acknowledgedSequence + (ulong)starvedTicks;
        const ulong RepeatTicks = (ulong)BufferedPlayerCommandInbox.ServerRepeatTicks;
        // Newest sample whose commands since the ack are exactly what the server simulated.
        int exactIndex = acknowledgedIndex;
        bool exact = true;
        // Newest sample whose commands since the ack are the ack's movement, then idle.
        int matchingIndex = acknowledgedIndex;
        bool matching = true;
        bool clientIdled = false;
        for (int index = acknowledgedIndex + 1; index < _history.Count && (exact || matching); index++)
        {
            Sample sample = _history[index];
            if (sample.Sequence > target || sample.Sequence != _history[index - 1].Sequence + 1)
            {
                break;
            }

            if (!sample.HasMove || !acknowledged.HasMove)
            {
                // A caller that records no movement: assumed to be what the server repeated.
                exactIndex = exact ? index : exactIndex;
                matchingIndex = matching ? index : matchingIndex;
                continue;
            }

            bool serverRepeats = sample.Sequence - acknowledgedSequence <= RepeatTicks;
            bool oneShot = sample.Move.JumpPressed || sample.Move.FreezePressed || sample.Move.ToggleNoclipPressed;
            bool sameAsAck = BufferedPlayerCommandInbox.HasSameMovement(sample.Move, acknowledged.Move);
            bool idle = BufferedPlayerCommandInbox.HasSameMovement(sample.Move, PlayerMoveIntent.None);
            exact &= !oneShot && (serverRepeats ? sameAsAck : idle);
            if (exact)
            {
                exactIndex = index;
            }

            if (matching)
            {
                if (oneShot)
                {
                    matching = false;
                }
                else if (sameAsAck && serverRepeats && !clientIdled)
                {
                    matchingIndex = index;
                }
                else if (idle)
                {
                    clientIdled = true;
                    matchingIndex = index;
                }
                else
                {
                    matching = false;
                }
            }
        }

        if (matching && _history[matchingIndex].Sequence == target)
        {
            return _history[matchingIndex].Position;
        }

        Sample from = _history[exactIndex];
        Vector3 step = exactIndex > 0 && _history[exactIndex - 1].Sequence + 1 == from.Sequence
            ? from.Position - _history[exactIndex - 1].Position
            : Vector3.Zero;
        // Falling or jumping (not flying in noclip, whose vertical axis moves it at a set speed):
        // each further tick's vertical displacement changes by gravity, as the motor's body does.
        bool airborne = MathF.Abs(step.Y) > 0.5f * GravityStepPerTick
            && (!acknowledged.HasMove || acknowledged.Move.VerticalAxis == 0f);
        Vector3 position = from.Position;
        float stepY = step.Y;
        for (ulong repeat = Math.Max(from.Sequence, acknowledgedSequence) - acknowledgedSequence + 1; repeat <= (ulong)starvedTicks; repeat++)
        {
            if (airborne)
            {
                stepY -= GravityStepPerTick;
            }

            position += repeat <= RepeatTicks
                ? new Vector3(step.X, stepY, step.Z)
                : new Vector3(0f, stepY, 0f);
        }

        return position;
    }

    private int FindLatestSampleAtOrBefore(ulong sequence)
    {
        for (int index = _history.Count - 1; index >= 0; index--)
        {
            if (_history[index].Sequence <= sequence)
            {
                return index;
            }
        }

        return -1;
    }
}
