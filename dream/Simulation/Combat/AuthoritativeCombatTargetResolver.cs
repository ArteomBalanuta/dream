using OpenTK.Mathematics;

namespace Dream.Simulation.Combat;

public static class AuthoritativeCombatTargetResolver
{
    public static CombatActorHandle? TryResolve(
        Vector3 origin,
        Vector3 direction,
        float maxHitDistance,
        IEnumerable<CombatTargetCandidate> candidates)
    {
        if (direction.LengthSquared <= 0.0001f || maxHitDistance <= 0f)
        {
            return null;
        }

        Vector3 normalizedDirection = direction.Normalized();
        CombatActorHandle? bestActor = null;
        int bestPriority = int.MaxValue;
        float bestDistance = maxHitDistance;

        foreach (CombatTargetCandidate candidate in candidates)
        {
            if (!candidate.Actor.Damageable.Health.IsAlive)
            {
                continue;
            }

            Vector3 toTarget = candidate.Anchor - origin;
            float projectedDistance = Vector3.Dot(toTarget, normalizedDirection);
            if (projectedDistance < 0f || projectedDistance > bestDistance)
            {
                continue;
            }

            Vector3 closestPoint = origin + (normalizedDirection * projectedDistance);
            if ((candidate.Anchor - closestPoint).Length > 0.9f)
            {
                continue;
            }

            if (candidate.Priority > bestPriority)
            {
                continue;
            }

            if (candidate.Priority == bestPriority && projectedDistance >= bestDistance)
            {
                continue;
            }

            bestActor = candidate.Actor;
            bestPriority = candidate.Priority;
            bestDistance = projectedDistance;
        }

        return bestActor;
    }
}
