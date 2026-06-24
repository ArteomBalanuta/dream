using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;

public class HudRenderer
{
    private readonly Render _render;

    public HudRenderer(Render render)
    {
        _render = render;
    }

    public HudFrameResult DrawFrame(FrameEventArgs args, bool windowFocused, GameSettings settings, MouseState mouseState)
    {
        return _render.DrawHUD(args, windowFocused, settings, mouseState);
    }

    public void UpdateProjectionForSettings(GameSettings settings)
    {
        _render.UpdateProjectionForSettings(settings);
    }
}
