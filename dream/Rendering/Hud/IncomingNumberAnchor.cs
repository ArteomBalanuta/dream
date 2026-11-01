using Dream.Application;
using OpenTK.Mathematics;
using Dream.Simulation.Combat;

namespace Dream.Rendering.Hud;

// Where the local player's own incoming damage number stands in the classic HUD. The player's combat
// anchor sits at (or right behind) the first-person camera, so projecting it drops the number. A
// number on the local player in first person, or that cannot be projected,
// stands at a fixed spot on screen instead: lower centre, between the crosshair and the bar. In
// third person the anchor is a few metres away and projects over the character as before.
public static class IncomingNumberAnchor
{
    // Fraction of the viewport height: below the crosshair, well above the bar.
    public const float ScreenYFraction = 0.62f;

    // Decided by view mode, not by distance: the anchor is frozen where the hit happened while the
    // camera keeps moving, so a distance test would flip as the player walks away from the spot.
    public static bool UsesScreenAnchor(FloatingCombatTextKind kind, CameraViewMode viewMode, bool projected) =>
        kind == FloatingCombatTextKind.Incoming && (viewMode == CameraViewMode.FirstPerson || !projected);

    public static Vector2 ScreenAnchor(int viewportWidth, int viewportHeight) =>
        new(viewportWidth * 0.5f, viewportHeight * ScreenYFraction);
}
