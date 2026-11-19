using System.Text;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Dream.Rendering.Core;

// A debug group name, encoded once as NUL-terminated UTF-8. Call sites keep one in a static
// readonly field so pushing a group never allocates.
public sealed class GpuDebugLabel
{
    internal readonly byte[] Utf8;

    public GpuDebugLabel(string name)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        int length = Encoding.UTF8.GetByteCount(name);
        Utf8 = new byte[length + 1];
        Encoding.UTF8.GetBytes(name, 0, name.Length, Utf8, 0);
    }

    public string Name { get; }
}

// `using (GpuDebug.Scope(label)) { ... }`. Dispose pops. A struct, so it allocates nothing.
public readonly struct GpuDebugScope : IDisposable
{
    public void Dispose() => GpuDebug.Pop();
}

// KHR_debug groups and object labels, so a RenderDoc or Nsight capture reads by pass name. Optional:
// nothing happens until Initialize runs on the render thread with a current context and finds
// the extension. GLUtils' debug callback only enables HIGH and MEDIUM severities, so push and pop
// notifications are never reported.
public static unsafe class GpuDebug
{
    public const string EnvironmentVariable = "DREAM_GL_MARKERS";

    private const uint DebugSourceApplication = 0x824A;

    private static bool s_enabled;
    private static int s_depth;
    private static delegate* unmanaged<uint, uint, int, byte*, void> s_push;
    private static delegate* unmanaged<void> s_pop;

    public static bool IsEnabled => s_enabled;

    public static void Initialize()
    {
        bool supported = SupportsKhrDebug();
        if (supported)
        {
            s_push = (delegate* unmanaged<uint, uint, int, byte*, void>)GLFW.GetProcAddress("glPushDebugGroup");
            s_pop = (delegate* unmanaged<void>)GLFW.GetProcAddress("glPopDebugGroup");
            supported = s_push != null && s_pop != null;
        }

        bool debugBuild = false;
#if DEBUG
        debugBuild = true;
#endif
        s_depth = 0;
        s_enabled = ResolveEnabled(Environment.GetEnvironmentVariable(EnvironmentVariable), debugBuild,
            RenderDocHook.IsAttached, supported);
        Console.WriteLine($"[startup] gl markers: {(s_enabled ? "on" : supported ? "off" : "unsupported")}");
    }

    public static bool ResolveEnabled(string? environmentValue, bool debugBuild, bool renderDocAttached, bool supported)
    {
        if (!supported)
        {
            return false;
        }

        return environmentValue switch
        {
            "0" => false,
            "1" => true,
            _ => debugBuild || renderDocAttached,
        };
    }

    public static void Push(GpuDebugLabel label)
    {
        if (!s_enabled)
        {
            return;
        }

        fixed (byte* message = label.Utf8)
        {
            s_push(DebugSourceApplication, 0, -1, message);
        }

        s_depth++;
    }

    public static void Pop()
    {
        if (!s_enabled || s_depth == 0)
        {
            return;
        }

        s_depth--;
        s_pop();
    }

    public static GpuDebugScope Scope(GpuDebugLabel label)
    {
        Push(label);
        return default;
    }

    public static void LabelObject(ObjectLabelIdentifier identifier, int handle, string name)
    {
        if (!s_enabled)
        {
            return;
        }

        GL.ObjectLabel(identifier, handle, ObjectLabelByteLength(name), name);
    }

    // GL counts the label length in bytes of the UTF-8 it is handed, not UTF-16 chars.
    internal static int ObjectLabelByteLength(string name) => Encoding.UTF8.GetByteCount(name);

    // Test seam: drops the static state so a test that initialized on a hidden context leaks nothing.
    internal static void ResetForTests()
    {
        s_enabled = false;
        s_depth = 0;
        s_push = null;
        s_pop = null;
    }

    internal static int DepthForTests => s_depth;

    private static bool SupportsKhrDebug()
    {
        int major = GL.GetInteger(GetPName.MajorVersion);
        int minor = GL.GetInteger(GetPName.MinorVersion);
        if (major > 4 || (major == 4 && minor >= 3))
        {
            return true;
        }

        int count = GL.GetInteger(GetPName.NumExtensions);
        for (int i = 0; i < count; i++)
        {
            if (GL.GetString(StringNameIndexed.Extensions, i) == "GL_KHR_debug")
            {
                return true;
            }
        }

        return false;
    }
}
