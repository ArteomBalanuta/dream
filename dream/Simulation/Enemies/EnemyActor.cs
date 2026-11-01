using OpenTK.Mathematics;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Simulation.World;

namespace Dream.Simulation.Enemies;


public sealed class EnemyActor
{
    private const float StandOffRangeFraction = 0.9f;

    public EnemyActor(
        EnemyCombatant combatant,
        Vector3 startPosition,
        float aggroRange,
        float attackRange,
        float moveSpeed,
        float preferredStandOffDistance = 0f,
        float collisionRadius = 0.45f,
        float collisionHeight = 1.2f,
        string displayName = "Wolf",
        string enemyKind = "Wolf",
        int level = 1,
        string? visualKey = null)
    {
        Combatant = combatant;
        Position = startPosition;
        Forward = -Vector3.UnitZ;
        AggroRange = Math.Max(attackRange, aggroRange);
        AttackRange = Math.Max(0.1f, attackRange);
        MoveSpeed = Math.Max(0f, moveSpeed);
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

    public Vector3 Forward { get; private set; }

    public float AggroRange { get; }

    public float AttackRange { get; }

    public float MoveSpeed { get; }

    public float PreferredStandOffDistance { get; }

    public float CollisionRadius { get; }

    public float CollisionHeight { get; }

    public string DisplayName { get; }

    public string EnemyKind { get; }

    public int Level { get; }

    // The client's model and tint key (wave I); null is the default visual (the wolf).
    public string? VisualKey { get; }

    public EnemyBehaviorState BehaviorState { get; private set; }

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

    private void UpdateCore(
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
            BehaviorState = EnemyBehaviorState.Idle;
            return;
        }

        Vector3 toPlayer = playerPosition.Value - Position;
        toPlayer.Y = 0f;
        float distanceToPlayer = toPlayer.Length;
        Vector3 direction = distanceToPlayer > 0.0001f ? toPlayer / distanceToPlayer : Vector3.Zero;

        if (distanceToPlayer <= AttackRange)
        {
            FaceDirection(direction);
            BehaviorState = EnemyBehaviorState.Attacking;
            return;
        }

        if (distanceToPlayer <= AggroRange)
        {
            BehaviorState = EnemyBehaviorState.Chasing;
            float allowedStep = Math.Max(0f, distanceToPlayer - PreferredStandOffDistance);
            float step = Math.Min(MoveSpeed * Math.Max(0f, deltaTime), allowedStep);
            if (step > 0f && direction != Vector3.Zero)
            {
                Vector3 desiredDelta = direction * step;
                Vector3 resolvedDelta = map != null
                    ? EnemyMovementCollision.ResolveStep(map, Position, desiredDelta, CollisionRadius, CollisionHeight)
                    : resolveMovement?.Invoke(Position, desiredDelta) ?? desiredDelta;
                Position += resolvedDelta;
                FaceDirection(resolvedDelta);
            }

            return;
        }

        BehaviorState = EnemyBehaviorState.Idle;
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
