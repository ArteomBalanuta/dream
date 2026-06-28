using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;
using OpenTK.Mathematics;

public class HudRenderer
{
    private readonly Render _render;
    private readonly FloatingCombatTextSystem _floatingCombatText = new();

    public HudRenderer(Render render)
    {
        _render = render;
    }

    public CombatFeedbackSystem CreateCombatFeedbackSystem(SceneEventHub events)
    {
        return new CombatFeedbackSystem(events, _floatingCombatText);
    }

    public HudFrameResult DrawFrame(FrameEventArgs args, bool windowFocused, GameSettings settings, MouseState mouseState, Camera camera)
    {
        return _render.DrawHUD(args, windowFocused, settings, mouseState, camera, _floatingCombatText);
    }

    public void UpdateProjectionForSettings(GameSettings settings)
    {
        _render.UpdateProjectionForSettings(settings);
    }

    public void Update(float deltaTime) => _floatingCombatText.Update(deltaTime);
}
