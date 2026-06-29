using OpenTK.Mathematics;

public sealed class EnemyRegistry
{
    private readonly List<EnemyActor> _actors = [];

    public IReadOnlyList<EnemyActor> ActiveActors => _actors;

    public bool HasActiveActors => _actors.Any(actor => actor.IsActive);

    public void Add(EnemyActor actor)
    {
        _actors.Add(actor);
    }

    public void Clear()
    {
        _actors.Clear();
    }

    public void UpdateAll(
        float deltaTime,
        Vector3 playerPosition,
        PlayerCombatant playerCombatant,
        SceneEventHub events,
        Map? map = null)
    {
        UpdateAll(
            deltaTime,
            [
                new AuthoritativePlayerTarget(
                    PlayerSessionId.LocalDefault,
                    playerCombatant.Actor.Id,
                    () => playerPosition,
                    () => -Vector3.UnitZ,
                    playerCombatant,
                    PlayerInteractionMode.Building,
                    _ => { })
            ],
            events,
            map);
    }

    public void UpdateAll(
        float deltaTime,
        IReadOnlyList<IAuthoritativePlayerTarget> playerTargets,
        SceneEventHub events,
        Map? map = null)
    {
        foreach (EnemyActor actor in _actors)
        {
            IAuthoritativePlayerTarget? target = ResolveNearestTarget(actor, playerTargets);
            actor.Update(
                deltaTime,
                target?.Position,
                map == null
                    ? null
                    : (from, desiredDelta) => EnemyMovementCollision.ResolveStep(
                        map,
                        from,
                        desiredDelta,
                        actor.CollisionRadius,
                        actor.CollisionHeight));
            actor.TryAttack(target?.Combatant, events);
        }
    }

    public EnemyActor? TryResolveHit(Vector3 origin, Vector3 direction, float maxHitDistance, float hitRadius = 0.9f)
    {
        if (direction.LengthSquared <= 0.0001f || maxHitDistance <= 0f)
        {
            return null;
        }

        Vector3 normalizedDirection = direction.Normalized();
        EnemyActor? best = null;
        float bestDistance = maxHitDistance;

        foreach (EnemyActor actor in _actors)
        {
            if (!actor.IsActive)
            {
                continue;
            }

            Vector3 target = actor.Combatant.GetCombatAnchorWorldPosition();
            Vector3 toTarget = target - origin;
            float projectedDistance = Vector3.Dot(toTarget, normalizedDirection);
            if (projectedDistance < 0f || projectedDistance > bestDistance)
            {
                continue;
            }

            Vector3 closestPoint = origin + (normalizedDirection * projectedDistance);
            float distanceFromRay = (target - closestPoint).Length;
            if (distanceFromRay > hitRadius)
            {
                continue;
            }

            best = actor;
            bestDistance = projectedDistance;
        }

        return best;
    }

    public void PruneInactive()
    {
        _actors.RemoveAll(actor => !actor.IsActive);
    }

    public void ClampToMapBounds(Map map)
    {
        foreach (EnemyActor actor in _actors)
        {
            Vector3 position = actor.Position;
            Vector3 clamped = new(
                Math.Clamp(position.X, 0.5f, map.Width - 0.5f),
                Math.Clamp(position.Y, 0f, map.Height - 0.5f),
                Math.Clamp(position.Z, 0.5f, map.Depth - 0.5f));
            actor.SetPosition(clamped);
        }
    }

    public bool TryGetActorById(int actorId, out CombatActorHandle actor)
    {
        EnemyActor? match = _actors.FirstOrDefault(candidate => candidate.Combatant.Actor.Id == actorId);
        if (match == null)
        {
            actor = default;
            return false;
        }

        actor = match.Combatant.Actor;
        return true;
    }

    private static IAuthoritativePlayerTarget? ResolveNearestTarget(
        EnemyActor actor,
        IReadOnlyList<IAuthoritativePlayerTarget> playerTargets)
    {
        IAuthoritativePlayerTarget? bestTarget = null;
        float bestDistanceSquared = float.MaxValue;

        foreach (IAuthoritativePlayerTarget target in playerTargets)
        {
            if (!target.IsSpawned || !target.IsAlive)
            {
                continue;
            }

            float distanceSquared = (target.Position - actor.Position).LengthSquared;
            if (distanceSquared >= bestDistanceSquared)
            {
                continue;
            }

            bestTarget = target;
            bestDistanceSquared = distanceSquared;
        }

        return bestTarget;
    }
}
