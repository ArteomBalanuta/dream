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
// commands replayed on top (just the server position when nothing was acknowledged). Error is
// the server position minus the prediction for the acknowledged command.

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
public sealed class ClientPredictionReconciler
{
    public const float DefaultDeadbandDistance = 0.05f;
    public const float DefaultSnapDistance = 3f;
    public const float DefaultSmoothingSeconds = 0.1f;
    private const int MaxHistory = 256;

    private readonly List<(ulong Sequence, Vector3 Position)> _history = [];
    private readonly float _deadbandDistance;
    private readonly float _snapDistance;
    private readonly float _smoothingSeconds;
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
        float smoothingSeconds = DefaultSmoothingSeconds)
    {
        _deadbandDistance = deadbandDistance;
        _snapDistance = snapDistance;
        _smoothingSeconds = smoothingSeconds;
    }

    public Vector3 PendingOffset => _pendingOffset;

    public int HistoryCount => _history.Count;

    // Call after each local simulation tick with the command sequence that tick used and the
    // player's simulated position.
    public void RecordPrediction(ulong commandSequence, Vector3 simulatedPosition)
    {
        // Record where the player will be once pending smoothing is applied, so samples taken
        // mid-blend compare against the corrected path.
        Vector3 intendedPosition = simulatedPosition + _pendingOffset;
        if (_history.Count > 0 && _history[^1].Sequence == commandSequence)
        {
            _history[^1] = (commandSequence, intendedPosition);
            return;
        }

        if (_history.Count > 0 && commandSequence < _history[^1].Sequence)
        {
            return;
        }

        _history.Add((commandSequence, intendedPosition));
        if (_history.Count > MaxHistory)
        {
            _history.RemoveAt(0);
        }
    }

    public ClientReconciliationResult Reconcile(ulong? acknowledgedSequence, Vector3 serverPosition)
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

        Vector3 predicted = _history[sampleIndex].Position;
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
            _history[index] = (_history[index].Sequence, _history[index].Position + error);
        }
    }

    private ClientReconciliationResult Snap(Vector3 serverPosition)
    {
        Reset();
        return new ClientReconciliationResult(ClientReconciliationKind.Snap, Vector3.Zero, serverPosition);
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
