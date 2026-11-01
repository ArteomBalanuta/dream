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
}
