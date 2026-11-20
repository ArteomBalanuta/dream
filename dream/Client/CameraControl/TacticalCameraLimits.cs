using Dream.Client.Presentation;
using Dream.Rendering.Shadows;
using Dream.Simulation.World;
using OpenTK.Mathematics;

namespace Dream.Client.CameraControl;

// The zoom-out and pan limits the renderer can honour, derived from the view and the shadow and export volumes
// (never restated as numbers). The footprint is the ground the four view corners see around the view target.
// Everything is in the camera's own frame (right, forward on the ground), so it does not depend on the yaw.
public static class TacticalCameraLimits
{
    // The widest window shape the game supports (21:9). The footprint grows with the aspect, so limits use it.
    public const float ReferenceAspect = 2.333f;

    // The farthest the camera ever zooms out, whatever the shadow volume allows.
    public const float MaxDistanceCap = 30f;

    // The smallest leash, so the camera can always roam a little.
    public const float MinLeashMetres = 8f;

    // Largest distance (<= MaxDistanceCap) whose ground footprint at the reference aspect fits inside the shadow
    // receiver circle. A static: it depends only on constants.
    public static float MaxDistance { get; } = ComputeMaxDistance();

    // How far the view target may stray from the player: the export, centred on the target, still covers the
    // footprint at MaxDistance.
    public static float LeashMetres { get; } =
        MathF.Max(WorldVisibility.MaxRenderDistance - FootprintRadius(MaxDistance, ReferenceAspect), MinLeashMetres);

    // The farthest horizontal distance from the view target to a place where one of the four view-corner rays meets
    // the target's ground plane. A ray that does not meet it within the far plane counts as the far-plane distance.
    public static float FootprintRadius(float distance, float aspect)
    {
        float radius = 0f;
        foreach (Vector2 corner in FootprintCorners(distance, aspect))
        {
            radius = MathF.Max(radius, corner.Length);
        }

        return radius;
    }

    // The four ground hits relative to the view target: X right, Y forward (screen up on the ground).
    public static Vector2[] FootprintCorners(float distance, float aspect)
    {
        float pitch = MathHelper.DegreesToRadians(IsometricView.PitchDegrees);
        float tanY = MathF.Tan(MathHelper.DegreesToRadians(IsometricView.FieldOfViewDegrees) * 0.5f);
        float tanX = tanY * aspect;
        float sin = MathF.Sin(pitch);
        float cos = MathF.Cos(pitch);
        float height = distance * sin;
        float back = distance * cos;
        float far = IsometricView.FarPlaneFor(distance);

        Vector2[] corners = new Vector2[4];
        int index = 0;
        foreach (float sx in new[] { -1f, 1f })
        {
            foreach (float sy in new[] { -1f, 1f })
            {
                // Ray = view direction + sx*tanX*right + sy*tanY*up, in (right, forward, world up).
                float rayRight = sx * tanX;
                float rayForward = cos + (sy * tanY * sin);
                float rayUp = -sin + (sy * tanY * cos);
                float rayLength = MathF.Sqrt((rayRight * rayRight) + (rayForward * rayForward) + (rayUp * rayUp));

                if (rayUp < -1e-6f)
                {
                    float t = height / -rayUp;
                    if (t * rayLength <= far)
                    {
                        corners[index++] = new Vector2(t * rayRight, (t * rayForward) - back);
                        continue;
                    }
                }

                // No hit inside the far plane: the far-plane distance along the ray's ground direction.
                Vector2 flat = new(rayRight, rayForward);
                corners[index++] = flat.LengthSquared > 1e-12f ? flat.Normalized() * far : new Vector2(0f, far);
            }
        }

        return corners;
    }

    // True when every footprint corner lies within OrthoSide / 2 of the shadow receiver's centre (ReceiverCentreOffset
    // ahead of the target along the view's ground forward). A circle, not a square: the sun ortho is aligned with the
    // sun's azimuth, which moves, so only the radius is orientation-free.
    public static bool FootprintFitsReceiver(float distance, float aspect)
    {
        float radius = ShadowSettings.OrthoSide * 0.5f;
        Vector2 centre = new(0f, ShadowSettings.ReceiverCentreOffset);
        foreach (Vector2 corner in FootprintCorners(distance, aspect))
        {
            if ((corner - centre).Length > radius)
            {
                return false;
            }
        }

        return true;
    }

    private static float ComputeMaxDistance()
    {
        if (FootprintFitsReceiver(MaxDistanceCap, ReferenceAspect))
        {
            return MaxDistanceCap;
        }

        // The footprint grows with the distance, so bisect between a distance that fits and one that does not.
        float fits = 0f;
        float fails = MaxDistanceCap;
        for (int i = 0; i < 40; i++)
        {
            float mid = (fits + fails) * 0.5f;
            if (FootprintFitsReceiver(mid, ReferenceAspect))
            {
                fits = mid;
            }
            else
            {
                fails = mid;
            }
        }

        return fits;
    }
}
