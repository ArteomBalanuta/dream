using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Graphics.OpenGL4;

public class Game : GameWindow
{
    private Render _render;
    private Camera _camera;
    private Map _map;

    public Game() : base(GameWindowSettings.Default, NativeWindowSettings.Default)
    {
        _render = new Render();
        _map = new Map();
        _camera = new Camera(new Vector3(5, 2, 5), _map); // Start camera above the floor

        // Lock the mouse to the center of the window
        CursorState = CursorState.Grabbed;
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);

        // Clear the screen
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        // Draw the floor
        _render.DrawFloor(_camera);

        // Swap buffers
        SwapBuffers();
    }

    protected override void OnUpdateFrame(FrameEventArgs args)
    {
        base.OnUpdateFrame(args);

        // Handle input and update camera
        _camera.Update((float)args.Time, KeyboardState, MouseState);
    }

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);

        // Adjust the viewport to the new window size
        GL.Viewport(0, 0, e.Width, e.Height);
    }
}