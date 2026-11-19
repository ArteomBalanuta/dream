using System.Runtime.InteropServices;

namespace Dream.Rendering.Core;

// In-application RenderDoc API (renderdoc_app.h, API 1.1.2). Windows only. Every failure logs one line
// and leaves IsAttached false; nothing here throws.
public static unsafe class RenderDocHook
{
    public const string EnvironmentVariable = "DREAM_RENDERDOC";
    public const string CaptureFrameEnvironmentVariable = "DREAM_RENDERDOC_CAPTURE_FRAME";

    private const string LibraryName = "renderdoc.dll";
    private const int ApiVersion112 = 10102; // eRENDERDOC_API_Version_1_1_2

    // Slot indices in RENDERDOC_API_1_1_2 (renderdoc_app.h): the struct is a flat run of function
    // pointers in this order: GetAPIVersion 0, SetCaptureOptionU32 1, SetCaptureOptionF32 2,
    // GetCaptureOptionU32 3, GetCaptureOptionF32 4, SetFocusToggleKeys 5, SetCaptureKeys 6,
    // GetOverlayBits 7, MaskOverlayBits 8, RemoveHooks 9, UnloadCrashHandler 10,
    // SetCaptureFilePathTemplate 11, GetCaptureFilePathTemplate 12, GetNumCaptures 13, GetCapture 14,
    // TriggerCapture 15, IsTargetControlConnected 16, LaunchReplayUI 17, SetActiveWindow 18,
    // StartFrameCapture 19, IsFrameCapturing 20, EndFrameCapture 21.
    private const int TriggerCaptureSlot = 15;

    private static delegate* unmanaged[Cdecl]<void> s_trigger;
    private static long s_captureFrame = -1;
    private static bool s_triggered;

    public static bool IsAttached { get; private set; }

    internal static string? ResolveLibraryPath(string? env, bool alreadyLoaded, string programFiles)
    {
        if (alreadyLoaded)
        {
            return LibraryName;
        }

        if (string.IsNullOrWhiteSpace(env) || env == "0")
        {
            return null;
        }

        return env == "1" ? Path.Combine(programFiles, "RenderDoc", LibraryName) : env;
    }

    public static bool TryAttach(Func<string, string?> environment)
    {
        try
        {
            if (IsAttached)
            {
                return true;
            }

            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            nint module = GetModuleHandleW(LibraryName);
            string? path = ResolveLibraryPath(environment(EnvironmentVariable), module != 0,
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            if (path == null)
            {
                return false;
            }

            if (module == 0)
            {
                module = LoadLibraryW(path);
                if (module == 0)
                {
                    Console.WriteLine($"[startup] renderdoc: could not load {path}");
                    return false;
                }
            }

            nint getApi = GetProcAddress(module, "RENDERDOC_GetAPI");
            if (getApi == 0)
            {
                Console.WriteLine("[startup] renderdoc: RENDERDOC_GetAPI not found");
                return false;
            }

            void* api = null;
            if (((delegate* unmanaged[Cdecl]<int, void**, int>)getApi)(ApiVersion112, &api) != 1 || api == null)
            {
                Console.WriteLine("[startup] renderdoc: API 1.1.2 unavailable");
                return false;
            }

            s_trigger = (delegate* unmanaged[Cdecl]<void>)((void**)api)[TriggerCaptureSlot];
            if (s_trigger == null)
            {
                Console.WriteLine("[startup] renderdoc: TriggerCapture missing");
                return false;
            }

            s_captureFrame = long.TryParse(environment(CaptureFrameEnvironmentVariable), out long frame) && frame > 0
                ? frame
                : -1;
            IsAttached = true;
            Console.WriteLine("[startup] renderdoc: attached");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[startup] renderdoc: attach failed ({ex.GetType().Name}: {ex.Message})");
            IsAttached = false;
            return false;
        }
    }

    public static void TriggerCapture()
    {
        if (!IsAttached)
        {
            return;
        }

        try
        {
            s_trigger();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[renderdoc] trigger failed ({ex.GetType().Name})");
        }
    }

    // Call after SwapBuffers. Triggers once at frame N-1 so that frame N is the one captured.
    public static void OnFrameEnd(long frameIndex)
    {
        if (!IsAttached || s_triggered || s_captureFrame < 0 || frameIndex != s_captureFrame - 1)
        {
            return;
        }

        s_triggered = true;
        TriggerCapture();
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint GetModuleHandleW(string name);

    [DllImport("kernel32", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint LoadLibraryW(string path);

    [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern nint GetProcAddress(nint module, string name);
}
