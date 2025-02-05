// Game.cs
using BulletSharp;
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

        Vector3 playerStartPos = new Vector3(5, 1.5f, 5); // Adjust based on corrected positioning

        _player = new Player(_map, _physics, _physics.PlayerBody, playerStartPos);
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

        Console.Title = $"Player Position: {_player.LastReportedPosition}";
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

       // _physics.UpdatePlayerCollisionCamera(_player); // doesn work
        // Render all cubes
        foreach (var cube in _map.Cubes)
        {
            if (cube.IsSolid)
                _render.DrawCube(_player.Camera, cube);
        }

        // Render falling cube
        //_physics.FallingCubeBody.MotionState.GetWorldTransform(out Matrix fallTrans);
        //_render.DrawCube(_player.Camera, fallTrans.Origin.ToOpenTK(), new Cube());

        // Render physics collision walls
        foreach (var body in _physics.MapCubeBodies)
        {
            if (body.MotionState is DefaultMotionState motionState)
            {
                motionState.GetWorldTransform(out Matrix transform);
                //Matrix4 modelMatrix = transform.ToOpenTK();

                /* TODO : analyze this! */ 
                Matrix4 modelMatrix =
transform.ToOpenTK()
* Matrix4.CreateScale(1)
* Matrix4.CreateTranslation(new Vector3(-0.5f, -0.5f, -0.5f));


                
                _render.DrawCollisionShape(modelMatrix, _player.Camera);
            }
        }

        // Also draw player bounding box
        if (_physics.PlayerBody.MotionState is DefaultMotionState playerMotion)
        {
            playerMotion.GetWorldTransform(out Matrix playerTransform);
            //Matrix4 playerModelMatrix = playerTransform.ToOpenTK();

            Matrix4 modelMatrix =
playerTransform.ToOpenTK()
* Matrix4.CreateScale(1)
* Matrix4.CreateTranslation(new Vector3(-0.5f, -0.5f, -0.5f));

            _render.DrawCollisionShape(modelMatrix, _player.Camera);
        }

       
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

public static class BulletSharpExtensions
{
    public static Matrix4 ToOpenTK(this Matrix bulletMatrix)
    {
        return new Matrix4(
            bulletMatrix.M11, bulletMatrix.M12, bulletMatrix.M13, bulletMatrix.M14,
            bulletMatrix.M21, bulletMatrix.M22, bulletMatrix.M23, bulletMatrix.M24,
            bulletMatrix.M31, bulletMatrix.M32, bulletMatrix.M33, bulletMatrix.M34,
            bulletMatrix.M41, bulletMatrix.M42, bulletMatrix.M43, bulletMatrix.M44
        );
    }
}