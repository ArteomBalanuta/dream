using Dream.Rendering.Hud;

namespace Dream.Rendering.Ui;

// Runs one classic frame so that a throwing panel can never leave a half-collected request behind.
// On a fault the surface's pending action and close flag are dropped (nothing from the failed frame
// is sent to the server), the caller's fault handler latches the ImGui surface, and the frame reports
// that nothing was drawn.
public static class ClassicHudFrameGuard
{
    public static bool Draw(
        ClassicHudSurface surface,
        in ClassicHudFrame frame,
        out InventoryHudAction? action,
        out bool closeInventory,
        Action<Exception> onFault)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(onFault);
        action = null;
        closeInventory = false;
        try
        {
            if (!surface.Draw(in frame))
            {
                return false;
            }

            surface.TakeRequests(out action, out closeInventory);
            return true;
        }
        catch (Exception ex)
        {
            surface.TakeRequests(out _, out _);
            action = null;
            closeInventory = false;
            onFault(ex);
            return false;
        }
    }
}
