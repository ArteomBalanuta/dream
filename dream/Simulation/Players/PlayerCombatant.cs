using OpenTK.Mathematics;
using Dream.Rendering.Hud;
using Dream.Simulation.Combat;
using Dream.Simulation.Runtime;

namespace Dream.Simulation.Players;


public sealed class PlayerCombatant : IDamageable, ICombatAnchorProvider
{
    private readonly Func<Vector3> _positionProvider;
    private readonly float _anchorHeightOffset;
    private readonly SceneEventHub? _eventHub;
    private float _attackCooldownRemaining;

    public PlayerCombatStats Stats { get; }

    public PlayerCombatDerivedStats DerivedStats { get; private set; }

    public HealthState Health { get; }

    public CombatActorHandle Actor { get; }

    public PlayerCombatant(
        Func<Vector3> positionProvider,
        SceneEventHub? eventHub = null,
        int actorId = 1,
        float anchorHeightOffset = 1.2f,
        PlayerCombatStats? stats = null)
    {
        _positionProvider = positionProvider;
        _eventHub = eventHub;
        _anchorHeightOffset = anchorHeightOffset;
        Stats = stats ?? new PlayerCombatStats();
        DerivedStats = Stats.Derive();
        Health = new HealthState(DerivedStats.MaxHealth);
        Actor = new CombatActorHandle(actorId, CreatureKind.Player, Health, this);
    }

    public DamageReceipt ApplyDamage(int amount)
    {
        DamageReceipt receipt = Health.ApplyDamage(amount);
        if (receipt.WasFatal)
        {
            _eventHub?.Publish(new ActorDiedEvent(Actor));
        }

        return receipt;
    }

    public void AdvanceCombat(float deltaTime)
    {
        _attackCooldownRemaining = Math.Max(0f, _attackCooldownRemaining - Math.Max(0f, deltaTime));
    }

    public void ApplyStats(PlayerCombatStats updatedStats)
    {
        Stats.Strength = updatedStats.Strength;
        Stats.AttackSpeed = updatedStats.AttackSpeed;
        Stats.Vitality = updatedStats.Vitality;

        DerivedStats = Stats.Derive();
        Health.SetMaxHealthPreservingRatio(DerivedStats.MaxHealth);
    }

    public bool TryStartAttack()
    {
        if (_attackCooldownRemaining > 0f)
        {
            return false;
        }

        _attackCooldownRemaining = DerivedStats.AttackIntervalSeconds;
        return true;
    }

    public PlayerAttackResolution ResolveAttack(Func<int> damageRoll, Func<double> critRoll)
    {
        if (critRoll() <= DerivedStats.CriticalChance)
        {
            return new PlayerAttackResolution(
                DerivedStats.CriticalDamage,
                true,
                FloatingCombatTextKind.Critical);
        }

        int damage = Math.Clamp(damageRoll(), DerivedStats.MinDamage, DerivedStats.MaxDamage);
        return new PlayerAttackResolution(
            damage,
            false,
            FloatingCombatTextKind.Regular);
    }

    public Vector3 GetCombatAnchorWorldPosition()
    {
        return _positionProvider() + new Vector3(0f, _anchorHeightOffset, 0f);
    }
}
