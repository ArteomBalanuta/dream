// Game.cs
using BulletSharp;
using BulletSharp.Math;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Vector3 = OpenTK.Mathematics.Vector3;

public class Game : GameWindow
{
    private Render _render;
    private Player _player;
    private Map _map;
    private PhysicsIntegrator _physics;
    private readonly GameSettings _settings;
    private bool _wasSettingsTogglePressed;

    private double _lastTime;
    private int _frameCount;
    public int FPS { get; private set; }

    public Game() : base(GameWindowSettings.Default, new NativeWindowSettings
    {
        ClientSize = new Vector2i(1440, 1024),
        Title = "Physics Integrated Game"
    })
    {
        _settings = new GameSettings(new Vector2i(1440, 1024));
        _physics = new PhysicsIntegrator();
        _map = new Map(_physics, width: _settings.FloorSize, depth: _settings.FloorSize);

        Vector3 playerStartPos = new Vector3(5, 1.5f, 5); // Adjust based on corrected positioning

        _player = new Player(_map, _physics, _physics.PlayerBody, playerStartPos);
        _render = new Render(_settings);

        CursorState = CursorState.Grabbed;
    }

    public void UpdateFPS()
    {
        double currentTime = GLFW.GetTime(); // Get current time in seconds
        _frameCount++;

        if (currentTime - _lastTime >= 1.0) // Every second
        {
            FPS = _frameCount;
            _frameCount = 0;
            _lastTime = currentTime;
        }
    }



    protected override void OnUpdateFrame(FrameEventArgs args)
    {
        base.OnUpdateFrame(args);

        float delta = (float)args.Time;
        HandleGlobalInput();
        bool allowGameplayInput = !_settings.SettingsVisible;

        // Update physics first
        _physics.StepSimulation(delta);

        // Update player and input
        _player.Update(delta, KeyboardState, MouseState, _settings, allowGameplayInput);
        _player.PlaceCube(_map, MouseState, allowGameplayInput);

        // Update cube positions from physics
        foreach (var body in _physics.DynamicMapCubeBodies)
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
        foreach (var cube in _map.SolidCubes)
        {
            _render.DrawCube(_player.Camera, cube);
        }

        // Render falling cube
        //_physics.FallingCubeBody.MotionState.GetWorldTransform(out Matrix fallTrans);
        //_render.DrawCube(_player.Camera, fallTrans.Origin.ToOpenTK(), new Cube());


        // Render physics collision walls
        /*
        foreach (var body in _physics.MapCubeBodies)
        {
            if (body.MotionState is DefaultMotionState motionState)
            {
                motionState.GetWorldTransform(out Matrix transform);
                //Matrix4 modelMatrix = transform.ToOpenTK();


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
        */

        HudFrameResult hudResult = _render.DrawHUD(args, IsFocused, _settings, MouseState);
        if (hudResult.RequestedResolution.HasValue)
        {
            ClientSize = hudResult.RequestedResolution.Value;
            _settings.ApplyResolution(ClientSize);
            _render.UpdateProjectionForSettings(_settings);
        }
        else
        {
            _render.UpdateProjectionForSettings(_settings);
        }

        if (hudResult.RequestedFloorSize.HasValue && hudResult.RequestedFloorSize.Value != _settings.FloorSize)
        {
            _settings.StageFloorSize(hudResult.RequestedFloorSize.Value);
            _settings.ApplyPendingFloorSize();
            RebuildMapForFloorSize(_settings.FloorSize);
        }

        SwapBuffers();

        UpdateFPS();
        this.Title = "FPS: " + this.FPS.ToString();
    }

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        GL.Viewport(0, 0, e.Width, e.Height);
        _settings.ApplyResolution(new Vector2i(e.Width, e.Height));
        _render.UpdateProjectionForSettings(_settings);
       // _player.Camera.AspectRatio = (float)e.Width / e.Height;
    }

    protected override void OnUnload()
    {
        base.OnUnload();
        _physics.Dispose();
        //_render.Dispose();
    }

    private void HandleGlobalInput()
    {
        bool settingsTogglePressed = KeyboardState.IsKeyDown(Keys.Escape);
        if (settingsTogglePressed && !_wasSettingsTogglePressed)
        {
            _settings.ToggleSettings();
            CursorState = _settings.SettingsVisible ? CursorState.Normal : CursorState.Grabbed;
        }

        _wasSettingsTogglePressed = settingsTogglePressed;
    }

    private void RebuildMapForFloorSize(int floorSize)
    {
        Vector3 currentPlayerPosition = _player.GetWorldPosition();
        Map previousMap = _map;

        _physics.ResetMapBodies();

        var rebuiltMap = new Map(_physics, width: floorSize, depth: floorSize);
        previousMap.CopyPlacedCubesTo(rebuiltMap);

        _map = rebuiltMap;
        _player.map = rebuiltMap;

        Vector3 clampedPosition = new Vector3(
            Math.Clamp(currentPlayerPosition.X, 0.5f, rebuiltMap.Width - 0.5f),
            Math.Clamp(currentPlayerPosition.Y, 1.5f, rebuiltMap.Height - 0.5f),
            Math.Clamp(currentPlayerPosition.Z, 0.5f, rebuiltMap.Depth - 0.5f));

        _player.SetWorldPosition(clampedPosition);
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
