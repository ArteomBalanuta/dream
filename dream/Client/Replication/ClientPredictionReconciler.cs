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
// Accepted errors are applied as a pending offset that drains over SmoothingSeconds, and the
// remaining history is shifted by the same error so the next snapshot does not count it again.
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

        // Every recorded sample is newer than the ack (for example right after a snap, while the
        // server still lags behind): nothing to compare yet, so wait for a later snapshot.
        int sampleIndex = FindLatestSampleAtOrBefore(acknowledgedSequence.Value);
        if (sampleIndex < 0)
        {
            return new ClientReconciliationResult(ClientReconciliationKind.None, Vector3.Zero, serverPosition);
        }

        Vector3 predicted = _history[sampleIndex].Position;
        // Keep the acknowledged sample: the server can ack the same sequence again.
        _history.RemoveRange(0, sampleIndex);

        Vector3 error = serverPosition - predicted;
        float distance = error.Length;
        if (distance <= _deadbandDistance)
        {
            return new ClientReconciliationResult(ClientReconciliationKind.None, error, serverPosition);
        }

        if (distance >= _snapDistance)
        {
            return Snap(serverPosition);
        }

        for (int index = 0; index < _history.Count; index++)
        {
            _history[index] = (_history[index].Sequence, _history[index].Position + error);
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
