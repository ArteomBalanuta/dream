using OpenTK.Mathematics;

namespace Dream.Client.CameraControl;

// Clips the OS cursor to a screen rectangle. Release must be safe to call when nothing is confined.
public interface ICursorConfiner
{
    bool IsConfined { get; }

    // clientRectScreen: the window's client area in screen pixels (min inclusive, max exclusive).
    void Confine(Box2i clientRectScreen);

    void Release();
}
