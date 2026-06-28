using OpenTK.Mathematics;

public sealed class EnemyActor
{
    public EnemyActor(
        EnemyCombatant combatant,
        Vector3 startPosition,
        float aggroRange,
        float attackRange,
        float moveSpeed,
        float preferredStandOffDistance = 0f)
    {
        Combatant = combatant;
        Position = startPosition;
        Forward = -Vector3.UnitZ;
        AggroRange = Math.Max(attackRange, aggroRange);
        AttackRange = Math.Max(0.1f, attackRange);
        MoveSpeed = Math.Max(0f, moveSpeed);
        PreferredStandOffDistance = Math.Max(AttackRange, preferredStandOffDistance);
        combatant.SetPositionProvider(() => Position);
    }

    public EnemyCombatant Combatant { get; }

    public Vector3 Position { get; private set; }

    public Vector3 Forward { get; private set; }

    public float AggroRange { get; }

    public float AttackRange { get; }

    public float MoveSpeed { get; }

    public float PreferredStandOffDistance { get; }

    public EnemyBehaviorState BehaviorState { get; private set; }

    public EnemyVisualState VisualState { get; } = new();

    public bool IsActive => Combatant.Health.IsAlive;

    public void Update(float deltaTime, Vector3 playerPosition)
    {
        Combatant.AdvanceCombat(deltaTime);

        if (!Combatant.Health.IsAlive)
        {
            BehaviorState = EnemyBehaviorState.Dead;
            return;
        }

        Vector3 toPlayer = playerPosition - Position;
        toPlayer.Y = 0f;
        float distanceToPlayer = toPlayer.Length;
        Vector3 direction = distanceToPlayer > 0.0001f ? toPlayer / distanceToPlayer : Vector3.Zero;
        if (direction != Vector3.Zero)
        {
            Forward = direction;
        }

        if (distanceToPlayer <= AttackRange)
        {
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
                Position += direction * step;
            }

            return;
        }

        BehaviorState = EnemyBehaviorState.Idle;
    }

    public bool TryAttack(PlayerCombatant playerCombatant, SceneEventHub events)
    {
        if (BehaviorState != EnemyBehaviorState.Attacking || !Combatant.TryStartAttack())
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
}
