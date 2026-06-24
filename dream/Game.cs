// Game.cs
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

public class Game : GameWindow
{
    private readonly GameSettings _settings;
    private readonly PlayScene _playScene;

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
        _playScene = new PlayScene(_settings);
        CursorState = _playScene.DesiredCursorState;
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
        _playScene.Update(args, KeyboardState, MouseState);
        CursorState = _playScene.DesiredCursorState;
        Console.Title = $"Player Position: {_playScene.LastReportedPlayerPosition}";
    }

    protected override void OnRenderFrame(FrameEventArgs args)
    {
        base.OnRenderFrame(args);
        _playScene.Render(args, IsFocused, MouseState, size => ClientSize = size);
        SwapBuffers();

        UpdateFPS();
        this.Title = "FPS: " + this.FPS.ToString();
    }

    protected override void OnResize(ResizeEventArgs e)
    {
        base.OnResize(e);
        _playScene.Resize(new Vector2i(e.Width, e.Height));
    }

    protected override void OnUnload()
    {
        base.OnUnload();
        _playScene.Dispose();
    }
}
