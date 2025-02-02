using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Graphics.OpenGL4;

public class Game : GameWindow
{
    private float _totalTime;
    private Render _render;
    private Player _player;
    private Map _map;

    public Game() : base(GameWindowSettings.Default, NativeWindowSettings.Default)
    {
        //this.UpdateFrequency = 240; // fps kinda

        _render = new Render();
        _map = new Map();
        _player = new Player(new Vector3(5, 2, 5)); // Start player on the floor

        // Lock the mouse to the center of the window
        CursorState = CursorState.Grabbed;
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);

        // Clear the screen
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        // Draw all solid cubes in the map
        DrawCubes();


        // Swap buffers
        SwapBuffers();
    }

    private void DrawCubes()
    {
        // Iterate through the 3D array of cubes
        for (int x = 0; x < _map.SideSize; x++)
        {
            for (int y = 0; y < _map.SideSize; y++)
            {
                for (int z = 0; z < _map.SideSize; z++)
                {
                    // Check if the cube is solid
                    if (_map.Cubes[x, y, z].IsSolid)
                    {
                        // Calculate the position of the cube
                        Vector3 cubePosition = new Vector3(x, y, z);

                        // Draw the cube
                        _render.DrawCube(_player.Camera, cubePosition, _map.Cubes[x, y, z]);
                    }
                }
            }
        }
    }

    protected override void OnUpdateFrame(FrameEventArgs args)
    {
        base.OnUpdateFrame(args);

        // Handle input and update player (which also updates the camera)
        _player.Update((float)args.Time, _map, KeyboardState, MouseState);


        // fire anim
//        _totalTime += (float) args.Time;
//        _render.UpdateFireEffect(_totalTime);
    }

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);

        // Adjust the viewport to the new window size
        GL.Viewport(0, 0, e.Width, e.Height);
    }
}