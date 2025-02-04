// Game.cs
using BulletSharp.Math;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using Vector3 = OpenTK.Mathematics.Vector3;

public class Game : GameWindow
{
    private Render _render;
    private Player _player;
    private Map _map;
    private PhysicsIntegrator _physics;

    public Game() : base(GameWindowSettings.Default, new NativeWindowSettings
    {
        Size = new Vector2i(1440, 1024),
        Title = "Physics Integrated Game"
    })
    {
        _physics = new PhysicsIntegrator();
        _map = new Map(_physics);
        _player = new Player(_physics.PlayerBody, new Vector3(5, 2, 5));
        _render = new Render();

        CursorState = CursorState.Grabbed;
    }

    protected override void OnUpdateFrame(FrameEventArgs args)
    {
        base.OnUpdateFrame(args);
        float delta = (float)args.Time;

        // Update physics first
        _physics.StepSimulation(delta);

        // Update player and input
        _player.Update(delta, KeyboardState, MouseState);
        _player.PlaceCube(_map, MouseState);

        // Update cube positions from physics
        foreach (var body in _physics.MapCubeBodies)
        {
            if (body.UserObject is (Cube cube, Vector3 _))
            {
                body.MotionState.GetWorldTransform(out Matrix transform);
                cube.Position = transform.Origin.ToOpenTK();
            }
        }
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        // Render all cubes
        foreach (var cube in _map.Cubes)
        {
            if (cube.IsSolid)
                _render.DrawCube(_player.Camera, cube.Position, cube);
        }

        // Render falling cube
        _physics.FallingCubeBody.MotionState.GetWorldTransform(out Matrix fallTrans);
        _render.DrawCube(_player.Camera, fallTrans.Origin.ToOpenTK(), new Cube());

        SwapBuffers();
    }

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        GL.Viewport(0, 0, e.Width, e.Height);
       // _player.Camera.AspectRatio = (float)e.Width / e.Height;
    }

    protected override void OnUnload()
    {
        base.OnUnload();
        _physics.Dispose();
        //_render.Dispose();
    }
}