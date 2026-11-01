using OpenTK.Mathematics;
using Dream.Simulation.Combat;
using Dream.Simulation.Runtime;
using Dream.Simulation.Stats;

namespace Dream.Simulation.Enemies;


public sealed class EnemyCombatant : IDamageable, ICombatAnchorProvider
{
    private Func<Vector3> _positionProvider;
    private readonly float _anchorHeightOffset;
    private readonly SceneEventHub? _eventHub;
    private float _attackCooldownRemaining;

    public EnemyCombatant(
        Func<Vector3> positionProvider,
        SceneEventHub? eventHub = null,
        int actorId = 2000,
        int maxHealth = 40,
        int attackDamage = 10,
        float attackIntervalSeconds = 1.1f,
        float anchorHeightOffset = WolfDefinition.CombatAnchorHeight,
        StatBlock? baseStats = null)
    {
        _positionProvider = positionProvider;
        _eventHub = eventHub;
        _anchorHeightOffset = anchorHeightOffset;
        AttackDamage = Math.Max(1, attackDamage);
        AttackIntervalSeconds = Math.Max(0.05f, attackIntervalSeconds);
        Health = new HealthState(maxHealth);
        Actor = new CombatActorHandle(actorId, CreatureKind.Enemy, this, this);

        // An enemy's stats are instance stats (wave I). Without an explicit block (a test's scripted
        // enemy) the sheet holds what every persisted enemy row used to be written with.
        StatBlock block;
        if (baseStats.HasValue)
        {
            block = baseStats.Value;
        }
        else
        {
            block = default;
            block[StatId.Strength] = DefaultStrength;
            block[StatId.Agility] = DefaultAgility;
            block[StatId.Vitality] = DefaultVitality;
            block[StatId.Energy] = DefaultEnergy;
            block[StatId.AttackSpeed] = 1f / Math.Max(0.05f, AttackIntervalSeconds);
        }

        StatSheet.SetBase(block);
    }

    public const int DefaultStrength = 10;
    public const int DefaultAgility = 10;
    public const int DefaultVitality = 10;
    public const int DefaultEnergy = 0;

    // Base stats plus (for now) no modifier sources. Damage, interval and maximum health are derived
    // from it once, by EnemyFactory, through the enemy.* formulas.
    public StatSheet StatSheet { get; } = new();

    public int AttackDamage { get; }

    public float AttackIntervalSeconds { get; }

    public HealthState Health { get; }

    public CombatActorHandle Actor { get; }

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

    public bool TryStartAttack()
    {
        if (_attackCooldownRemaining > 0f || !Health.IsAlive)
        {
            return false;
        }

        _attackCooldownRemaining = AttackIntervalSeconds;
        return true;
    }

    public void SetPositionProvider(Func<Vector3> positionProvider)
    {
        _positionProvider = positionProvider;
    }

    public Vector3 GetCombatAnchorWorldPosition()
    {
        return _positionProvider() + new Vector3(0f, _anchorHeightOffset, 0f);
    }
}
