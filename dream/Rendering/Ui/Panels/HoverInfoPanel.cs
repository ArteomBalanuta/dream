namespace Dream.Rendering.Ui.Panels;

// The compact info panel for the hovered world thing, drawn from HudView.HoverInfo and HudView.HoverPanel
// only. Non-interactive: it registers no widget, so it never counts as HUD under the pointer.
public static class HoverInfoPanel
{
    public static void Build(IUiCanvas c, IUiInput i, HudView v, in UiLayout l, UiSpriteTable s)
    {
        // world-hover stub: draws nothing until the panel lands.
    }
}
