namespace Dream.Client.Scenes;

// When the world a play session joined is ready to be shown: joined, a first snapshot applied, and
// WarmupFrames consecutive frames with no model pending and no terrain mesh rebuilt; or TimeoutSeconds
// after the join, so a stuck decode cannot trap the player. Pure and allocation-free per frame.
public sealed class WorldReadiness
{
    public const int WarmupFrames = 10;
    public const double TimeoutSeconds = 20.0;

    private bool _snapshot;
    private int _settled;
    private int _maxPending;
    private int _pending;
    private double _sinceJoin;

    public void ObserveJoinApplied()
    {
        if (Joined)
        {
            return;
        }

        Joined = true;
        _sinceJoin = 0;
        Recompute();
    }

    public void ObserveSnapshotApplied()
    {
        _snapshot = true;
        Recompute();
    }

    public void ObserveFrame(int pendingModels, int chunkMeshesRebuilt, double frameSeconds)
    {
        if (Ready)
        {
            return;
        }

        _pending = pendingModels;
        if (pendingModels > _maxPending)
        {
            _maxPending = pendingModels;
        }

        if (pendingModels == 0 && chunkMeshesRebuilt == 0)
        {
            if (_settled < WarmupFrames)
            {
                _settled++;
            }
        }
        else
        {
            _settled = 0;
        }

        if (Joined)
        {
            _sinceJoin += frameSeconds;
        }

        Recompute();
    }

    private void Recompute()
    {
        if (Ready)
        {
            return;
        }

        if (Joined && _sinceJoin > TimeoutSeconds)
        {
            TimedOut = true;
            Ready = true;
        }
        else if (Joined && _snapshot && _settled >= WarmupFrames)
        {
            Ready = true;
        }

        if (Ready)
        {
            Progress01 = 1f;
            Stage = "The world awaits.";
            return;
        }

        float m = Joined ? (_maxPending == 0 ? 1f : 1f - (float)_pending / _maxPending) : 0f;
        float w = MathF.Min(1f, _settled / (float)WarmupFrames);
        float p = (Joined ? 0.15f : 0f) + (_snapshot ? 0.10f : 0f) + 0.45f * m + 0.30f * w;
        p = MathF.Min(p, 0.999f);
        if (p > Progress01)
        {
            Progress01 = p;
        }

        Stage = !Joined ? "Receiving the world..."
            : !_snapshot ? "Finding your footing..."
            : _pending > 0 ? "Summoning the beasts..."
            : "Kindling the fires...";
    }

    public bool Joined { get; private set; }

    public bool Ready { get; private set; }

    public bool TimedOut { get; private set; }

    // 0.15 J + 0.10 S + 0.45 M + 0.30 W, latched (never decreases), 1 exactly when Ready.
    public float Progress01 { get; private set; }

    // Constant strings: "Receiving the world...", "Finding your footing...", "Summoning the beasts...",
    // "Kindling the fires...", "The world awaits."
    public string Stage { get; private set; } = "Receiving the world...";
}
