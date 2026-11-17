namespace Dream.Client.Menu;

// One forward-only bar across the whole start: the title menu's launcher phases fill 0..SceneStart,
// the world's warm-up fills SceneStart..1.
public static class LoadingProgress
{
    public const float SceneStart = 0.40f;

    // Idle/Failed 0, Waking .04, Starting .12, Connecting .30, Connected .40.
    public static float ForLauncher(SinglePlayerPhase phase) => phase switch
    {
        SinglePlayerPhase.Waking => 0.04f,
        SinglePlayerPhase.Starting => 0.12f,
        SinglePlayerPhase.Connecting => 0.30f,
        SinglePlayerPhase.Connected => SceneStart,
        _ => 0f
    };

    public static float ForScene(float scene01) => SceneStart + (1f - SceneStart) * Math.Clamp(float.IsNaN(scene01) ? 0f : scene01, 0f, 1f);
}
