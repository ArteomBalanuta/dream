using System.Runtime.InteropServices;
using Dream.Simulation.World;
using OpenTK.Graphics.OpenGL4;

namespace Dream.Rendering.Core;

// How bad the errors drained by CheckGLError were.
public enum GLFaultKind
{
    None = 0,
    // GL_INVALID_* and friends: a bug in a call, reported and otherwise ignored.
    Recoverable = 1,
    // GL_OUT_OF_MEMORY or GL_CONTEXT_LOST: GL state is undefined, so the window should close.
    Fatal = 2
}

public sealed class GLErrorException(ErrorCode error, string context)
    : Exception($"OpenGL error {error} in {context}")
{
    public ErrorCode Error { get; } = error;

    public string Context { get; } = context;
}

public sealed class GLDebugMessageException(DebugSource source, DebugType type, int id, DebugSeverity severity, string message)
    : Exception($"GL debug {severity} {type} from {source} (id {id}): {message}")
{
    public DebugSeverity Severity { get; } = severity;
}

public static class GLUtils
{
    public const string ErrorFaultSource = "gl-error";
    public const string DebugOutputFaultSource = "gl-debug";

    // glGetError keeps a flag per error kind, so a few calls drain it. The bound stops a lost
    // context, which may report CONTEXT_LOST on every call, from looping forever.
    public const int MaxErrorsDrainedPerCheck = 8;

    // The driver keeps a native pointer to this delegate, so it lives in a static readonly field:
    // rooted for the life of the process and never re-created per install. It points at a static
    // method, not a lambda, so it captures nothing.
    private static readonly DebugProc s_debugMessageCallback = OnDebugMessage;
    private static readonly Func<ErrorCode> s_getError = GL.GetError;
    private static IRenderFaultSink? s_debugSink;

    public static void BindRenderThread() => RenderThreadAffinity.Bind();

    public static void AssertRenderThread(string context) => RenderThreadAffinity.AssertCurrent(context);

    // Set by any check that drains GL_OUT_OF_MEMORY or GL_CONTEXT_LOST and never cleared in the
    // game. A caller that drops the result (Shader creation, say) would otherwise drain the flag
    // before the frame-end check could see it. GL runs on the render thread only, so the latch is
    // thread-static, like RenderCounters, and tests on other threads never see each other's.
    [ThreadStatic] private static bool s_fatalErrorLatched;

    public static bool FatalErrorLatched => s_fatalErrorLatched;

    // Drains the GL error flags and reports each one through RenderFaultLog.Shared. Returns Fatal
    // when a fatal error was seen by this or any earlier check on this thread, so the frame-end
    // check in Game sees a fatal error another caller drained.
    public static GLFaultKind CheckGLError(string context) =>
        WithFatalLatch(CheckGLError(context, s_getError, RenderFaultLog.Shared));

    // Raises a check's result to Fatal once a fatal error has been latched.
    public static GLFaultKind WithFatalLatch(GLFaultKind kind) =>
        s_fatalErrorLatched ? GLFaultKind.Fatal : kind;

    // Test seam: starts this thread over with no fatal error latched.
    internal static void ResetFatalErrorLatch() => s_fatalErrorLatched = false;

    public static GLFaultKind CheckGLError(string context, Func<ErrorCode> getError, IRenderFaultSink sink)
    {
        ArgumentNullException.ThrowIfNull(getError);
        ArgumentNullException.ThrowIfNull(sink);

        GLFaultKind worst = GLFaultKind.None;
        for (int i = 0; i < MaxErrorsDrainedPerCheck; i++)
        {
            ErrorCode error = getError();
            if (error == ErrorCode.NoError)
            {
                break;
            }

            GLFaultKind kind = Classify(error);
            sink.Report(ErrorFaultSource, new GLErrorException(error, context));
            if (kind > worst)
            {
                worst = kind;
            }
        }

        if (worst == GLFaultKind.Fatal)
        {
            s_fatalErrorLatched = true;
        }

        return worst;
    }

    public static GLFaultKind Classify(ErrorCode error) => error switch
    {
        ErrorCode.NoError => GLFaultKind.None,
        ErrorCode.OutOfMemory or ErrorCode.ContextLost => GLFaultKind.Fatal,
        _ => GLFaultKind.Recoverable
    };

    // KHR_debug output (core in GL 4.3, an extension before). Only high- and medium-severity
    // messages are enabled; they go to the sink. Returns false when the context has no KHR_debug.
    // Call with the context current, on the render thread. Meant for DEBUG builds, which also
    // request a debug context.
    public static bool InstallDebugOutput(IRenderFaultSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        AssertRenderThread("GL debug output install");
        if (!SupportsDebugOutput())
        {
            return false;
        }

        s_debugSink = sink;
        GL.Enable(EnableCap.DebugOutput);
        GL.Enable(EnableCap.DebugOutputSynchronous);
        GL.DebugMessageCallback(s_debugMessageCallback, IntPtr.Zero);
        GL.DebugMessageControl(DebugSourceControl.DontCare, DebugTypeControl.DontCare, DebugSeverityControl.DontCare, 0, (int[]?)null, false);
        GL.DebugMessageControl(DebugSourceControl.DontCare, DebugTypeControl.DontCare, DebugSeverityControl.DebugSeverityHigh, 0, (int[]?)null, true);
        GL.DebugMessageControl(DebugSourceControl.DontCare, DebugTypeControl.DontCare, DebugSeverityControl.DebugSeverityMedium, 0, (int[]?)null, true);
        // Clear whatever an unsupported enum above may have raised on an odd driver.
        while (GL.GetError() != ErrorCode.NoError)
        {
        }

        return true;
    }

    public static bool ShouldReportDebugMessage(DebugSeverity severity) =>
        severity is DebugSeverity.DebugSeverityHigh or DebugSeverity.DebugSeverityMedium;

    private static bool SupportsDebugOutput()
    {
        int major = GL.GetInteger(GetPName.MajorVersion);
        int minor = GL.GetInteger(GetPName.MinorVersion);
        if (major > 4 || (major == 4 && minor >= 3))
        {
            return true;
        }

        int extensionCount = GL.GetInteger(GetPName.NumExtensions);
        for (int i = 0; i < extensionCount; i++)
        {
            string extension = GL.GetString(StringNameIndexed.Extensions, i);
            if (extension is "GL_KHR_debug" or "GL_ARB_debug_output")
            {
                return true;
            }
        }

        return false;
    }

    // May run on a driver thread when output is asynchronous; RenderFaultLog is thread-safe.
    private static void OnDebugMessage(
        DebugSource source,
        DebugType type,
        int id,
        DebugSeverity severity,
        int length,
        IntPtr message,
        IntPtr userParam)
    {
        IRenderFaultSink? sink = s_debugSink;
        if (sink == null || !ShouldReportDebugMessage(severity))
        {
            return;
        }

        try
        {
            string text = message == IntPtr.Zero
                ? string.Empty
                : length >= 0 ? Marshal.PtrToStringAnsi(message, length) : Marshal.PtrToStringAnsi(message) ?? string.Empty;
            sink.Report(DebugOutputFaultSource, new GLDebugMessageException(source, type, id, severity, text));
        }
        catch (Exception)
        {
            // An exception must never unwind into the driver.
        }
    }
}
