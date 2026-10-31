using OpenTK.Mathematics;
using Dream.Simulation.Combat;
using Dream.Simulation.Inventory;
using Dream.Simulation.Runtime;
using Dream.Simulation.Rules;
using Dream.Simulation.Stats;

namespace Dream.Simulation.Players;


public sealed class PlayerCombatant : IDamageable, ICombatAnchorProvider
{
    private readonly Func<Vector3> _positionProvider;
    private readonly CombatFormulaService _combatFormulaService;
    private readonly float _anchorHeightOffset;
    private readonly SceneEventHub? _eventHub;
    private float _attackCooldownRemaining;
    private const int EquippedToolSourceSlot = 0;
    private readonly StatSheet _statSheet = new();
    private ToolStatModifier _equippedToolModifier;

    public PlayerCombatStats BaseStats { get; }

    public PlayerCombatStats Stats { get; }

    public StatSheet StatSheet => _statSheet;

    public PlayerCombatDerivedStats DerivedStats { get; private set; }

    public int Level { get; private set; } = 1;

    public int Experience { get; private set; }

    public HealthState Health { get; }

    public CombatActorHandle Actor { get; }

    public PlayerCombatant(
        Func<Vector3> positionProvider,
        SceneEventHub? eventHub = null,
        int actorId = 1,
        float anchorHeightOffset = 1.2f,
        PlayerCombatStats? stats = null,
        CombatFormulaService? combatFormulaService = null)
    {
        _positionProvider = positionProvider;
        _combatFormulaService = combatFormulaService ?? CombatFormulaService.Default;
        _eventHub = eventHub;
        _anchorHeightOffset = anchorHeightOffset;
        BaseStats = new PlayerCombatStats();
        Stats = new PlayerCombatStats();
        BaseStats.Strength = (stats ?? new PlayerCombatStats()).Strength;
        BaseStats.AttackSpeed = (stats ?? new PlayerCombatStats()).AttackSpeed;
        BaseStats.Vitality = (stats ?? new PlayerCombatStats()).Vitality;
        RecalculateEffectiveStats();
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

    public void ApplyBaseStats(PlayerCombatStats updatedStats)
    {
        BaseStats.Strength = updatedStats.Strength;
        BaseStats.AttackSpeed = updatedStats.AttackSpeed;
        BaseStats.Vitality = updatedStats.Vitality;

        RecalculateEffectiveStats();
    }

    public void ApplyStats(PlayerCombatStats updatedStats)
    {
        ApplyBaseStats(updatedStats);
    }

    public void ApplyProgression(int level, int experience)
    {
        Level = Math.Max(1, level);
        Experience = Math.Max(0, experience);
        RecalculateEffectiveStats();
    }

    public void ApplyEquippedToolModifier(ToolStatModifier modifier)
    {
        _equippedToolModifier = modifier;
        RecalculateEffectiveStats();
    }

    private void RecalculateEffectiveStats()
    {
        StatBlock baseBlock = default;
        baseBlock[StatId.Strength] = BaseStats.Strength;
        baseBlock[StatId.AttackSpeed] = BaseStats.AttackSpeed;
        baseBlock[StatId.Vitality] = BaseStats.Vitality;
        _statSheet.SetBase(baseBlock);

        Span<StatModifier> toolModifiers = stackalloc StatModifier[3];
        int count = 0;
        if (_equippedToolModifier.StrengthBonus != 0)
        {
            toolModifiers[count++] = new StatModifier(StatId.Strength, StatOp.Flat, _equippedToolModifier.StrengthBonus);
        }

        if (_equippedToolModifier.AttackSpeedBonus != 0f)
        {
            toolModifiers[count++] = new StatModifier(StatId.AttackSpeed, StatOp.Flat, _equippedToolModifier.AttackSpeedBonus);
        }

        if (_equippedToolModifier.VitalityBonus != 0)
        {
            toolModifiers[count++] = new StatModifier(StatId.Vitality, StatOp.Flat, _equippedToolModifier.VitalityBonus);
        }

        _statSheet.SetSource(EquippedToolSourceSlot, toolModifiers[..count]);

        ref readonly StatBlock effective = ref _statSheet.Effective;
        Stats.Strength = (int)effective[StatId.Strength];
        Stats.AttackSpeed = effective[StatId.AttackSpeed];
        Stats.Vitality = (int)effective[StatId.Vitality];
        DerivedStats = Stats.Derive(_combatFormulaService, Level);
        if (Health != null)
        {
            Health.SetMaxHealthPreservingRatio(DerivedStats.MaxHealth);
        }
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
