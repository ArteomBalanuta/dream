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

    private void UpdateCubeAnimations(float deltaTime)
    {
        for (int x = 0; x < _map.SideSize; x++)
        {
            for (int y = 0; y < _map.SideSize; y++)
            {
                for (int z = 0; z < _map.SideSize; z++)
                {
                    var cube = _map.Cubes[x, y, z];
                    if (cube.Animation != null)
                    {
                        cube.Animation.Update(deltaTime);
                    }
                }
            }
        }
    }

    private void DrawCubes()
    {
        for (int x = 0; x < _map.SideSize; x++)
        {
            for (int y = 0; y < _map.SideSize; y++)
            {
                for (int z = 0; z < _map.SideSize; z++)
                {
                    var cube = _map.Cubes[x, y, z];
                    if (!cube.IsSolid) continue;

                    Vector3 position = new Vector3(x, y, z);
                    _render.DrawCube(_player.Camera, position, cube);
                }
            }
        }
    }

    protected override void OnUpdateFrame(FrameEventArgs args)
    {
        base.OnUpdateFrame(args);
        float deltaTime = (float)args.Time;

        // Update player and animations
        _player.Update(deltaTime, _map, KeyboardState, MouseState);
        UpdateCubeAnimations(deltaTime);
    }

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);

        // Adjust the viewport to the new window size
        GL.Viewport(0, 0, e.Width, e.Height);
    }
}