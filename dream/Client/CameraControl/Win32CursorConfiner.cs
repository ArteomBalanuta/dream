using System.Runtime.InteropServices;
using OpenTK.Mathematics;

namespace Dream.Client.CameraControl;

public static class CursorConfiners
{
    public static ICursorConfiner CreateForPlatform() =>
        OperatingSystem.IsWindows() ? new Win32CursorConfiner() : new NullCursorConfiner();
}

// Clips the cursor with user32 ClipCursor. Release is idempotent and never throws.
public sealed class Win32CursorConfiner : ICursorConfiner
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
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

    public bool IsConfined { get; private set; }

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
            IsConfined = ClipCursorRect(ref rect);
        }
        catch (Exception)
        {
            IsConfined = false;
        }
    }

    public void Release()
    {
        if (!IsConfined)
        {
            return;
        }

        IsConfined = false;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                ClipCursorNull(IntPtr.Zero);
            }
        }
        catch (Exception)
        {
        }
    }
}
