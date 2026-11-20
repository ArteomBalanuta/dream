using Dream.Rendering.Core;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Dream.Client.CameraControl;

// Turns raw mouse, keys and window events into CameraInputFrames. Collect runs every Update and accumulates;
// Take runs once per rendered frame and resets the accumulators. Reads only the real mouse.
public sealed class CameraInputSampler
{
    public CameraInputSampler(TacticalCameraSettings settings)
    {
        throw new NotImplementedException("tactical-camera stub");
    }

    public TacticalCameraSettings Settings
    {
        get => throw new NotImplementedException("tactical-camera stub");
        set => throw new NotImplementedException("tactical-camera stub");
    }

    public bool RotateDragHeld => throw new NotImplementedException("tactical-camera stub");

    public void OnFocusChanged(bool focused, double nowSeconds) => throw new NotImplementedException("tactical-camera stub");

    public void OnCursorEnterLeave(bool inside, double nowSeconds) => throw new NotImplementedException("tactical-camera stub");

    public void Collect(
        MouseState mouse,
        KeyboardState keys,
        Vector2i clientSize,
        float uiLayoutScale,
        in WorldPointerState pointer,
        CameraGate gate,
        double nowSeconds) =>
        throw new NotImplementedException("tactical-camera stub");

    public CameraInputFrame Take() => throw new NotImplementedException("tactical-camera stub");
}
