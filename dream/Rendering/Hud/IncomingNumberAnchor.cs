using OpenTK.Mathematics;
using Dream.Simulation.Combat;

namespace Dream.Rendering.Hud;

// Where the local player's own incoming damage number stands in the classic HUD. The player's combat
// anchor sits at (or right behind) the first-person camera, so projecting it drops the number. A
// number on the local player that is projected from inside this distance, or cannot be projected,
// stands at a fixed spot on screen instead: lower centre, between the crosshair and the bar. In
// third person the anchor is a few metres away and projects over the character as before.
public static class IncomingNumberAnchor
{
    public const float NearCameraMeters = 1.5f;

    // Fraction of the viewport height: below the crosshair, well above the bar.
    public const float ScreenYFraction = 0.62f;

    public static bool UsesScreenAnchor(FloatingCombatTextKind kind, Vector3 worldAnchor, Vector3 cameraPosition, bool projected)
    {
        if (kind != FloatingCombatTextKind.Incoming)
        {
            return false;
        }

        return !projected || (worldAnchor - cameraPosition).LengthSquared < NearCameraMeters * NearCameraMeters;
    }

    public static Vector2 ScreenAnchor(int viewportWidth, int viewportHeight) =>
        new(viewportWidth * 0.5f, viewportHeight * ScreenYFraction);
}
