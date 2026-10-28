namespace Dream.Rendering.Core;

// Per-frame CPU-side GL call counters. PlayScene captures and resets them at the start of each
// frame, so the HUD shows the previous, complete frame (HUD draw included).
// GL runs on the render thread only, so the counters are thread-static: plain increments with no
// interlocking, and tests on other threads never see the render thread's counts.
public static class RenderCounters
{
    [ThreadStatic] private static int s_drawCalls;
    [ThreadStatic] private static int s_programBinds;
    [ThreadStatic] private static int s_uniformUploads;

    public static int DrawCalls => s_drawCalls;

    public static int ProgramBinds => s_programBinds;

    public static int UniformUploads => s_uniformUploads;

    // Call next to each glDraw* call site.
    public static void CountDraw() => s_drawCalls++;

    public static void CountDraws(int count) => s_drawCalls += count;

    public static void CountProgramBind() => s_programBinds++;

    public static void CountUniformUpload() => s_uniformUploads++;

    public static RenderCounterSnapshot Capture() => new(s_drawCalls, s_programBinds, s_uniformUploads);

    public static void Reset()
    {
        s_drawCalls = 0;
        s_programBinds = 0;
        s_uniformUploads = 0;
    }

    // Returns this frame's counts and starts the next frame from zero.
    public static RenderCounterSnapshot CaptureAndReset()
    {
        RenderCounterSnapshot snapshot = Capture();
        Reset();
        return snapshot;
    }
}

public readonly record struct RenderCounterSnapshot(int DrawCalls, int ProgramBinds, int UniformUploads);
