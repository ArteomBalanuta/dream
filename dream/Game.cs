using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Graphics.OpenGL4;
using BulletSharp.Math;
using Vector3 = OpenTK.Mathematics.Vector3;

public class Game : GameWindow
{
    private float _totalTime;
    private Render _render;
    private Player _player;
    private Map _map;
    private PhysicsIntegrator _physicsIntegrator;

    public Game() : base(GameWindowSettings.Default, new NativeWindowSettings()
    {
        Size = new Vector2i(1440, 1024),
        Title = "My 2K Game"
    })
    {
        _render = new Render();
        _physicsIntegrator = new PhysicsIntegrator(_map);
        _map = new Map(_physicsIntegrator);
        // Initialize the physics integrator with the map.
        

        _player = new Player(new Vector3(5, 2, 5));

        CursorState = CursorState.Grabbed;
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);

        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        // Draw map cubes at their (possibly updated) positions.
        RenderSolidMapCubes();

        // Draw the heavy falling cube.
        //DrawFallingCube();

        SwapBuffers();
    }

    /// <summary>
    /// Draws the map cubes using their current positions.
    /// </summary>
    private void RenderSolidMapCubes()
    {
        for (int x = 0; x < _map.SideSize; x++)
        {
            for (int y = 0; y < _map.SideSize; y++)
            {
                for (int z = 0; z < _map.SideSize; z++)
                {
                    var cube = _map.Cubes[x, y, z];
                    if (!cube.IsSolid) continue;
                    // Use the updated position from the physics simulation.
                    _render.DrawCube(_player.Camera, cube.Position, cube);
                }
            }
        }
    }

    /// <summary>
    /// Retrieves the falling cube’s position from physics and draws it.
    /// </summary>
    private void DrawFallingCube()
    {
        Matrix fallingCubeTransform;
        _physicsIntegrator.FallingCubeBody.MotionState.GetWorldTransform(out fallingCubeTransform);
        BulletSharp.Math.Vector3 fallingCubePos = fallingCubeTransform.Origin;
        // For demonstration, we create a temporary Cube to render the falling cube.

        Cube fallingCube = new Cube(false, true)
        {
            Position = fallingCubePos.ToOpenTK(),
            ShaderName = "cube"
        };

        _render.DrawCube(_player.Camera, fallingCube.Position, fallingCube);
    }

    /// <summary>
    /// Update physics and game logic.
    /// </summary>
    protected override void OnUpdateFrame(FrameEventArgs args)
    {
        base.OnUpdateFrame(args);
        float deltaTime = (float)args.Time;

        // Update player and animations.
        _player.Update(deltaTime, _map, KeyboardState, MouseState);
        UpdateCubeAnimations(deltaTime);

        // Step the physics simulation.
        _physicsIntegrator.StepSimulation(deltaTime);

        /// Updates each solid cube’s position using its corresponding physics body.
        // Update map cubes’ positions based on their physics bodies.
        foreach (var body in _physicsIntegrator.MapCubeBodies)
        {
            // Retrieve the stored map indices from the body.
            if (body.UserObject is Tuple<int, int, int> indices)
            {
                int x = indices.Item1;
                int y = indices.Item2;
                int z = indices.Item3;

                Matrix trans;
                body.MotionState.GetWorldTransform(out trans);
                _map.Cubes[x, y, z].Position = trans.Origin.ToOpenTK();
            }
        }
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

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        GL.Viewport(0, 0, e.Width, e.Height);
    }

    protected override void OnUnload()
    {
        base.OnUnload();
        _physicsIntegrator.Dispose();
        // Dispose other resources (shaders, buffers, etc.) as needed.
    }
}