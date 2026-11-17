namespace Dream.Client.Scenes;

// When the world a play session joined is ready to be shown: joined, a first snapshot applied, and
// WarmupFrames consecutive frames with no model pending and no terrain mesh rebuilt; or TimeoutSeconds
// after the join, so a stuck decode cannot trap the player. Pure and allocation-free per frame.
// CONTRACT-STUB: W2 replaces the bodies.
public sealed class WorldReadiness
{
    public const int WarmupFrames = 10;
    public const double TimeoutSeconds = 20.0;

    public void ObserveJoinApplied()
    {
    }

    public void ObserveSnapshotApplied()
    {
    }

    public void ObserveFrame(int pendingModels, int chunkMeshesRebuilt, double frameSeconds)
    {
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
