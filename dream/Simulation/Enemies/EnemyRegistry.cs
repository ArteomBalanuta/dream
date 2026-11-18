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

    // The radius around an enemy's anchor a ray counts as hitting it, unless the caller passes one.
    public const float DefaultHitRadiusMetres = 0.9f;

    public EnemyActor? TryResolveHit(Vector3 origin, Vector3 direction, float maxHitDistance, float hitRadius = DefaultHitRadiusMetres)
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

    // M4: whether anchor lies within range of origin (3D) and inside the cone around horizontalDir (a unit
    // vector with Y = 0). The cone test uses the horizontal offset only, so height does not narrow an arc; a
    // target straight above or below the origin counts as inside. Also used for players in a PvP arc.
    public static bool TryMeasureArc(
        Vector3 origin,
        Vector3 horizontalDir,
        float range,
        float minDot,
        Vector3 anchor,
        out float distanceSquared)
    {
        Vector3 offset = anchor - origin;
        distanceSquared = offset.LengthSquared;
        if (distanceSquared > range * range)
        {
            return false;
        }

        float horizontalLength = MathF.Sqrt((offset.X * offset.X) + (offset.Z * offset.Z));
        if (horizontalLength <= 0.0001f)
        {
            return true;
        }

        float dot = ((offset.X * horizontalDir.X) + (offset.Z * horizontalDir.Z)) / horizontalLength;
        return dot >= minDot;
    }

    // M4: appends up to maxTargets active enemies inside the arc, nearest first, actor id breaking ties.
    // Allocation-free given a list with capacity. The caller damages after this returns, because a kill
    // prunes the registry.
    public void CollectInArc(
        Vector3 origin,
        Vector3 horizontalDir,
        float range,
        float minDot,
        int maxTargets,
        List<EnemyActor> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        if (maxTargets <= 0)
        {
            return;
        }

        int start = into.Count;
        for (int i = 0; i < _actors.Count; i++)
        {
            EnemyActor actor = _actors[i];
            if (!actor.IsActive
                || !TryMeasureArc(origin, horizontalDir, range, minDot, actor.Combatant.GetCombatAnchorWorldPosition(), out float distanceSquared))
            {
                continue;
            }

            int at = into.Count;
            if (at - start >= maxTargets)
            {
                // Full: only an enemy ahead of the farthest kept one gets in.
                EnemyActor last = into[at - 1];
                float lastDistance = (last.Combatant.GetCombatAnchorWorldPosition() - origin).LengthSquared;
                if (!IsBefore(distanceSquared, actor, lastDistance, last))
                {
                    continue;
                }

                into.RemoveAt(at - 1);
                at--;
            }

            into.Add(actor);
            while (at > start)
            {
                EnemyActor previous = into[at - 1];
                float previousDistance = (previous.Combatant.GetCombatAnchorWorldPosition() - origin).LengthSquared;
                if (!IsBefore(distanceSquared, actor, previousDistance, previous))
                {
                    break;
                }

                into[at] = previous;
                into[at - 1] = actor;
                at--;
            }
        }
    }

    private static bool IsBefore(float distanceA, EnemyActor a, float distanceB, EnemyActor b)
    {
        return distanceA < distanceB
            || (distanceA == distanceB && a.Combatant.Actor.Id < b.Combatant.Actor.Id);
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
