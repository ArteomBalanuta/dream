namespace Dream.Rendering.Core;

// Seconds for time-driven shader effects (`uTime`, e.g. scrolling emissive), advanced once per
// update from the presentation clock. Wrapped so a float keeps its precision in a long session.
public static class RenderFrameClock
{
    public const double WrapSeconds = 4096d;

    public static float Seconds { get; private set; }

    public static void Advance(double seconds)
    {
        Seconds = (float)(seconds % WrapSeconds);
    }
}
