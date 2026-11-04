using OpenTK.Mathematics;

namespace Dream.Rendering.Ui;

// Whether a screen point is over the classic HUD: the bar always, and the inventory and character
// windows only while they are open. Pure, so the click-to-move gate is testable without a window.
public static class ClassicHudHitTest
{
    public static bool Over(in UiLayout layout, bool inventoryOpen, Vector2 position)
    {
        if (layout.Bar.Contains(position))
        {
            return true;
        }

        return inventoryOpen && (layout.Inventory.Contains(position) || layout.Character.Contains(position));
    }

    // Whether a release at position should reach the world: a finite point on the screen, clear of
    // the bar and of both windows, using the same boundary as Over. UiLayout has no rect for the
    // chat, party and quest plates; they are already click-through for click-to-move, so a release
    // over them counts as world too. That is the accepted, consistent limitation.
    public static bool OverWorld(in UiLayout layout, bool inventoryOpen, Vector2 position) =>
        float.IsFinite(position.X) && float.IsFinite(position.Y)
        && position.X >= 0f && position.Y >= 0f && position.X < layout.ViewportWidth && position.Y < layout.ViewportHeight
        && !Over(in layout, inventoryOpen, position);
}
