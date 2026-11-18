using OpenTK.Mathematics;
using Dream.Simulation.Enemies;
using Dream.Simulation.World;

namespace Dream.Simulation.Combat;


public static class CombatUseResolver
{
    public const float DefaultHitRange = 4f;

    // How far past the weapon's reach the primary attack's ray is cast. The ray finds the enemy near the
    // line of sight; the reach itself is then judged on the horizontal distance to the enemy's anchor.
    public const float RayHeadroomMetres = 2f;

    public static WorldObjectRecord? TryResolveDestructibleCubeHit(
        Map map,
        WorldObjectRegistry registry,
        Vector3 origin,
        Vector3 direction,
        float maxHitDistance = DefaultHitRange)
    {
        Cube? hitCube = map.TryGetFirstSolidCubeAlongRay(origin, direction, maxHitDistance);
        if (hitCube == null)
        {
            return null;
        }

        return registry.TryResolveCubeRecord(map, hitCube);
    }

    public static EnemyActor? TryResolveEnemyHit(
        EnemyRegistry enemies,
        Vector3 origin,
        Vector3 direction,
        float maxHitDistance = DefaultHitRange)
    {
        return enemies.TryResolveHit(origin, direction, maxHitDistance);
    }
}
