using OpenTK.Mathematics;

namespace Dream.Rendering.Effects;

// The scene-level rules for which blobs exist and where they sit, kept free of GL and the scene so
// they can be tested: players are posed at the capsule centre, dead remote players have no blob,
// enemies are sized from their collision radius and visual scale.
public static class FxShadowRules
{
    // A player pose is the capsule centre, this far above the feet.
    public const float PlayerPoseToFeet = 0.5f;

    public static Vector3 PlayerFeet(Vector3 pose)
    {
        return pose - Vector3.UnitY * PlayerPoseToFeet;
    }

    public static bool RemotePlayerCastsShadow(bool isAlive)
    {
        return isAlive;
    }

    public static float EnemyRadius(float collisionRadius, float visualScaleMultiplier)
    {
        return collisionRadius * FxShadowList.EnemyRadiusScale * visualScaleMultiplier;
    }
}
