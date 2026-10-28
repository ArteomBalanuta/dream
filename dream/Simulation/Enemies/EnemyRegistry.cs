using OpenTK.Mathematics;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Simulation.Enemies;


public sealed class EnemyRegistry
{
    private readonly List<EnemyActor> _actors = [];

    public IReadOnlyList<EnemyActor> ActiveActors => _actors;

    public bool HasActiveActors
    {
        get
        {
            for (int i = 0; i < _actors.Count; i++)
            {
                if (_actors[i].IsActive)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public void Add(EnemyActor actor)
    {
        _actors.Add(actor);
    }

    public void Clear()
    {
        _actors.Clear();
    }

    // Removes the enemy with this actor id, keeping the others' order (the order the tick updates
    // them in and the checksum hashes them in). Allocation-free. False when no enemy has the id.
    public bool Remove(int actorId)
    {
        for (int i = 0; i < _actors.Count; i++)
        {
            if (_actors[i].Combatant.Actor.Id == actorId)
            {
                _actors.RemoveAt(i);
                return true;
            }
        }

        return false;
    }

    public bool TryGetEnemyByActorId(int actorId, out EnemyActor? actor)
    {
        actor = FindByActorId(actorId);
        return actor != null;
    }

    // Compacts in place, keeping the survivors' order, so a client snapshot allocates nothing here.
    public void RemoveAllExcept(IReadOnlySet<int> activeActorIds)
    {
        int kept = 0;
        for (int i = 0; i < _actors.Count; i++)
        {
            EnemyActor actor = _actors[i];
            if (activeActorIds.Contains(actor.Combatant.Actor.Id))
            {
                _actors[kept++] = actor;
            }
        }

        _actors.RemoveRange(kept, _actors.Count - kept);
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
            actor.Update(deltaTime, target?.Position, map);
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
        _actors.RemoveAll(static actor => !actor.IsActive);
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
        EnemyActor? match = FindByActorId(actorId);
        if (match == null)
        {
            actor = default;
            return false;
        }

        actor = match.Combatant.Actor;
        return true;
    }

    private EnemyActor? FindByActorId(int actorId)
    {
        for (int i = 0; i < _actors.Count; i++)
        {
            if (_actors[i].Combatant.Actor.Id == actorId)
            {
                return _actors[i];
            }
        }

        return null;
    }

    // Indexed, so the interface list's enumerator is not boxed. The first of equally near targets
    // wins, so the caller's target order decides ties.
    private static IAuthoritativePlayerTarget? ResolveNearestTarget(
        EnemyActor actor,
        IReadOnlyList<IAuthoritativePlayerTarget> playerTargets)
    {
        IAuthoritativePlayerTarget? bestTarget = null;
        float bestDistanceSquared = float.MaxValue;

        for (int i = 0; i < playerTargets.Count; i++)
        {
            IAuthoritativePlayerTarget target = playerTargets[i];
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
