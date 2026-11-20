using System.Runtime.InteropServices;
using OpenTK.Mathematics;

namespace Dream.Client.CameraControl;

public static class CursorConfiners
{
    public static ICursorConfiner CreateForPlatform() =>
        OperatingSystem.IsWindows() ? new Win32CursorConfiner() : new NullCursorConfiner();
}

// Clips the cursor with user32 ClipCursor. Release is idempotent and never throws. The clip is global to the
// desktop, so it is only cleared while it is still ours, and "confined" means the system still holds our rectangle
// (Windows drops a clip on a focus change).
public sealed class Win32CursorConfiner : ICursorConfiner
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "ClipCursor", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClipCursorRect(ref Rect rect);

    [DllImport("user32.dll", EntryPoint = "ClipCursor", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClipCursorNull(IntPtr rect);

    [DllImport("user32.dll", EntryPoint = "GetClipCursor", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClipCursorRect(out Rect rect);

    private bool _requested;
    private Rect _lastRect;

    public bool IsConfined
    {
        get
        {
            if (!_requested || !OperatingSystem.IsWindows())
            {
                return false;
            }

            return TryReadClip(out Rect current) && IsOurClip(true, _lastRect, current);
        }
    }

    // The clip is ours only if we asked for one and the system's current clip is exactly that rectangle.
    internal static bool IsOurClip(bool requested, Rect ours, Rect current) =>
        requested
        && ours.Left == current.Left && ours.Top == current.Top
        && ours.Right == current.Right && ours.Bottom == current.Bottom;

    public void Confine(Box2i clientRectScreen)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            Rect rect = new()
            {
                Left = clientRectScreen.Min.X,
                Top = clientRectScreen.Min.Y,
                Right = clientRectScreen.Max.X,
                Bottom = clientRectScreen.Max.Y
            };
            if (ClipCursorRect(ref rect))
            {
                _lastRect = rect;
                _requested = true;
            }
            else
            {
                _requested = false;
            }
        }
        catch (Exception)
        {
            _requested = false;
        }
    }

    public void Release()
    {
        if (!_requested)
        {
            return;
        }

        _requested = false;
        try
        {
            if (OperatingSystem.IsWindows() && TryReadClip(out Rect current) && IsOurClip(true, _lastRect, current))
            {
                ClipCursorNull(IntPtr.Zero);
            }
        }
        catch (Exception)
        {
        }
    }

    private static bool TryReadClip(out Rect current)
    {
        current = default;
        try
        {
            return GetClipCursorRect(out current);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
