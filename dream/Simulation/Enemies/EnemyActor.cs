using OpenTK.Mathematics;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Simulation.Enemies;


// One player's damage to one enemy, as kept for the kill's reward split.
public readonly record struct EnemyDamageContribution(PlayerSessionId SessionId, int Damage);

public sealed class EnemyActor
{
    // Most players tracked per enemy. When full, a newcomer replaces the lowest contributor only if it
    // has dealt more than that one.
    public const int MaxContributors = 8;

    public const float StandOffRangeFraction = 0.9f;

    // Half the player's body height (a 1 m box centred on its position). The wolf bites when the gap between
    // the two bodies' vertical extents is at most BiteVerticalReach, so a player standing on a 1-cube step
    // is in reach and one on a 2-cube step, on a pillar or above a floor is not (wolves never change Y).
    public const float PlayerHalfHeight = 0.5f;
    public const float BiteVerticalReach = 0.5f;

    // The leash: a wolf with no target walks back to its home and stops within this distance of it.
    private const float HomeArrivalDistance = 0.05f;

    public EnemyActor(
        EnemyCombatant combatant,
        Vector3 startPosition,
        float aggroRange,
        float attackRange,
        float moveSpeed,
        float patrolSpeed = 0f,
        float preferredStandOffDistance = 0f,
        float collisionRadius = 0.45f,
        float collisionHeight = 1.2f,
        string displayName = "Wolf",
        string enemyKind = "Wolf",
        int level = 1,
        string? visualKey = null,
        Vector3? homePosition = null)
    {
        Combatant = combatant;
        Position = startPosition;
        HomePosition = homePosition ?? startPosition;
        Forward = -Vector3.UnitZ;
        AggroRange = Math.Max(attackRange, aggroRange);
        AttackRange = Math.Max(0.1f, attackRange);
        MoveSpeed = Math.Max(0f, moveSpeed);
        // The walk home. 0 (unauthored, and every caller that predates patrol speeds) is half the
        // chase speed, the same default ContentCatalog gives an enemy with no "patrolSpeed".
        PatrolSpeed = patrolSpeed > 0f ? Math.Min(patrolSpeed, MoveSpeed) : MoveSpeed * 0.5f;
        // The stop point lies inside the attack range (90% of it at most), so a wolf that has stopped can bite.
        PreferredStandOffDistance = Math.Min(
            preferredStandOffDistance > 0f ? preferredStandOffDistance : AttackRange,
            AttackRange * StandOffRangeFraction);
        CollisionRadius = Math.Max(0.1f, collisionRadius);
        CollisionHeight = Math.Max(0.1f, collisionHeight);
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Enemy" : displayName.Trim();
        EnemyKind = string.IsNullOrWhiteSpace(enemyKind) ? "Enemy" : enemyKind.Trim();
        Level = Math.Max(1, level);
        VisualKey = visualKey;
        combatant.SetPositionProvider(() => Position);
    }

    public EnemyCombatant Combatant { get; }

    public Vector3 Position { get; private set; }

    // Where the wolf idles when it has no target (its spawn point; a restored wolf gets its role's spawn).
    // Not persisted and not part of any checksum.
    public Vector3 HomePosition { get; }

    public Vector3 Forward { get; private set; }

    public float AggroRange { get; }

    public float AttackRange { get; }

    // THE CHASE SPEED: what this enemy runs at while it is closing on a player. Persistence maps it
    // from and to PersistedCreatureStats.MovementSpeed, so its name and meaning are load-bearing.
    public float MoveSpeed { get; }

    // The walk it returns home at, at most MoveSpeed. Not persisted: it comes from the template.
    public float PatrolSpeed { get; }

    // Metres actually travelled per second over the last Update - after collision, so a wolf
    // grinding along a cube reports what it really covered, not what it asked for. 0 when standing.
    public float CurrentSpeed { get; private set; }

    public float PreferredStandOffDistance { get; }

    public float CollisionRadius { get; }

    public float CollisionHeight { get; }

    public string DisplayName { get; }

    public string EnemyKind { get; }

    public int Level { get; }

    // The client's model and tint key (wave I); null is the default visual (the wolf).
    public string? VisualKey { get; }

    public EnemyBehaviorState BehaviorState { get; private set; }

    private readonly EnemyDamageContribution[] _contributors = new EnemyDamageContribution[MaxContributors];
    private int _contributorCount;

    // Who has hurt this enemy and by how much, in first-hit order. Fixed storage: recording a hit
    // allocates nothing. Not persisted; a restored enemy starts with none.
    public ReadOnlySpan<EnemyDamageContribution> Contributors => new(_contributors, 0, _contributorCount);

    public void RecordDamage(PlayerSessionId sessionId, int amount)
    {
        if (amount <= 0)
        {
            return;
        }

        int lowest = 0;
        for (int i = 0; i < _contributorCount; i++)
        {
            if (_contributors[i].SessionId == sessionId)
            {
                long total = (long)_contributors[i].Damage + amount;
                _contributors[i] = new EnemyDamageContribution(sessionId, (int)Math.Min(total, int.MaxValue));
                return;
            }

            if (_contributors[i].Damage < _contributors[lowest].Damage)
            {
                lowest = i;
            }
        }

        if (_contributorCount < MaxContributors)
        {
            _contributors[_contributorCount++] = new EnemyDamageContribution(sessionId, amount);
        }
        else if (amount > _contributors[lowest].Damage)
        {
            _contributors[lowest] = new EnemyDamageContribution(sessionId, amount);
        }
    }

    // Drops a session's entry (it detached; a later session reusing the id must not inherit its credit).
    public void ClearContributor(PlayerSessionId sessionId)
    {
        for (int i = 0; i < _contributorCount; i++)
        {
            if (_contributors[i].SessionId != sessionId)
            {
                continue;
            }

            _contributorCount--;
            for (int j = i; j < _contributorCount; j++)
            {
                _contributors[j] = _contributors[j + 1];
            }

            _contributors[_contributorCount] = default;
            return;
        }
    }

    public bool IsActive => Combatant.Health.IsAlive;

    public void Update(
        float deltaTime,
        Vector3? playerPosition,
        Func<Vector3, Vector3, Vector3>? resolveMovement = null)
    {
        UpdateCore(deltaTime, playerPosition, map: null, resolveMovement);
    }

    // The server tick's overload: chase steps are resolved against the map's collision directly,
    // so no resolver delegate or closure is allocated per enemy per tick. A null map moves freely.
    public void Update(float deltaTime, Vector3? playerPosition, Map? map)
    {
        UpdateCore(deltaTime, playerPosition, map, resolveMovement: null);
    }

    // Measures what the behaviour step actually moved, then runs it. CurrentSpeed is what the enemy
    // really covered this tick; the client measures its own enemies from the drawn pose instead,
    // because it never runs this.
    private void UpdateCore(
        float deltaTime,
        Vector3? playerPosition,
        Map? map,
        Func<Vector3, Vector3, Vector3>? resolveMovement)
    {
        Vector3 before = Position;
        UpdateBehavior(deltaTime, playerPosition, map, resolveMovement);
        Vector3 moved = Position - before;
        moved.Y = 0f;
        CurrentSpeed = deltaTime > 0f ? moved.Length / deltaTime : 0f;
    }

    private void UpdateBehavior(
        float deltaTime,
        Vector3? playerPosition,
        Map? map,
        Func<Vector3, Vector3, Vector3>? resolveMovement)
    {
        Combatant.AdvanceCombat(deltaTime);

        if (!Combatant.Health.IsAlive)
        {
            BehaviorState = EnemyBehaviorState.Dead;
            return;
        }

        if (!playerPosition.HasValue)
        {
            ReturnHome(deltaTime, map, resolveMovement);
            return;
        }

        Vector3 toPlayer = playerPosition.Value - Position;
        float verticalOffset = toPlayer.Y;
        toPlayer.Y = 0f;
        float distanceToPlayer = toPlayer.Length;
        Vector3 direction = distanceToPlayer > 0.0001f ? toPlayer / distanceToPlayer : Vector3.Zero;

        // A bite needs the player within the horizontal range, within vertical reach (the wolf never leaves
        // its level, so a player on a pillar or above a floor is out of reach) and no cube in between.
        // A wolf out of reach keeps closing in horizontally, like a chase, and idles where it stands.
        if (distanceToPlayer <= AttackRange && CanBite(playerPosition.Value, verticalOffset, map))
        {
            FaceDirection(direction);
            BehaviorState = EnemyBehaviorState.Attacking;
            return;
        }

        if (distanceToPlayer <= AggroRange)
        {
            float allowedStep = Math.Max(0f, distanceToPlayer - PreferredStandOffDistance);
            float step = Math.Min(MoveSpeed * Math.Max(0f, deltaTime), allowedStep);
            BehaviorState = distanceToPlayer > AttackRange ? EnemyBehaviorState.Chasing : EnemyBehaviorState.Idle;
            if (step > 0f && direction != Vector3.Zero)
            {
                BehaviorState = EnemyBehaviorState.Chasing;
                MoveBy(direction * step, map, resolveMovement);
            }

            return;
        }

        ReturnHome(deltaTime, map, resolveMovement);
    }

    private bool CanBite(Vector3 playerPosition, float verticalOffset, Map? map)
    {
        // Gap between the wolf's vertical extent [0, CollisionHeight] and the player's [dy - h, dy + h].
        float gap = Math.Max(verticalOffset - PlayerHalfHeight - CollisionHeight, -verticalOffset - PlayerHalfHeight);
        if (gap > BiteVerticalReach)
        {
            return false;
        }

        return map == null
            || EnemyMovementCollision.HasClearBiteLine(map, Position, CollisionHeight * 0.5f, playerPosition, PlayerHalfHeight);
    }

    // No target (or none in aggro range): walk back to the home point at the PATROL speed - the chase speed is
    // for closing on a player, and an enemy that patrolled at it would never read as prowling - stop on arrival and
    // idle there. A step that is blocked leaves the wolf where it is. Deterministic, allocation-free.
    private void ReturnHome(float deltaTime, Map? map, Func<Vector3, Vector3, Vector3>? resolveMovement)
    {
        Vector3 toHome = HomePosition - Position;
        toHome.Y = 0f;
        float distanceToHome = toHome.Length;
        if (distanceToHome <= HomeArrivalDistance)
        {
            BehaviorState = EnemyBehaviorState.Idle;
            return;
        }

        float step = Math.Min(PatrolSpeed * Math.Max(0f, deltaTime), distanceToHome);
        Vector3 before = Position;
        if (step > 0f)
        {
            MoveBy(toHome / distanceToHome * step, map, resolveMovement);
        }

        BehaviorState = Position != before ? EnemyBehaviorState.Chasing : EnemyBehaviorState.Idle;
    }

    private void MoveBy(Vector3 desiredDelta, Map? map, Func<Vector3, Vector3, Vector3>? resolveMovement)
    {
        Vector3 resolvedDelta = map != null
            ? EnemyMovementCollision.ResolveStep(map, Position, desiredDelta, CollisionRadius, CollisionHeight)
            : resolveMovement?.Invoke(Position, desiredDelta) ?? desiredDelta;
        Position += resolvedDelta;
        FaceDirection(resolvedDelta);
    }

    public bool TryAttack(PlayerCombatant? playerCombatant, SceneEventHub events)
    {
        if (playerCombatant == null
            || BehaviorState != EnemyBehaviorState.Attacking
            || !Combatant.TryStartAttack())
        {
            return false;
        }

        DamageReceipt receipt = playerCombatant.ApplyDamage(Combatant.AttackDamage);
        if (receipt.AppliedAmount > 0)
        {
            events.Publish(new DamageAppliedEvent(
                playerCombatant.Actor,
                receipt.AppliedAmount,
                FloatingCombatTextKind.Regular));
        }

        return receipt.AppliedAmount > 0;
    }

    public void SetPosition(Vector3 position)
    {
        Position = position;
    }

    public void SetFacing(Vector3 facing)
    {
        FaceDirection(facing);
    }

    public void SetAuthoritativeState(Vector3 position, Vector3 facing, EnemyBehaviorState behaviorState)
    {
        Position = position;
        FaceDirection(facing);
        BehaviorState = behaviorState;
    }

    private void FaceDirection(Vector3 direction)
    {
        direction.Y = 0f;
        if (direction.LengthSquared <= 0.000001f)
        {
            return;
        }

        Forward = direction.Normalized();
    }
}
