using OpenTK.Mathematics;
using Dream.Simulation.Enemies;
using Dream.Simulation.World;

namespace Dream.Simulation.Combat;


public static class CombatUseResolver
{
    public const float DefaultHitRange = 4f;

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
