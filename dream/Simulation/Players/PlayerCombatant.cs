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
    private readonly StatSheet _statSheet = new();

    public PlayerCombatStats BaseStats { get; }

    public PlayerCombatStats Stats { get; }

    public StatSheet StatSheet => _statSheet;

    // The Armor the equipment adds up to (base 0, effective range 0..1000). In wave H it is only
    // read and shown: no damage calculation uses it yet.
    public int EffectiveArmor => (int)_statSheet.Effective[StatId.Armor];

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

    // Sets all four equipment sources (source slot = EquipmentSlot value: 0 main hand, 1 off hand,
    // 2 head, 3 body, resolved in ascending order), then derives once, so max health is rescaled a
    // single time. The loadout's modifiers are already screened by EquipmentLoadout.Set.
    public void ApplyEquipment(EquipmentLoadout equipment)
    {
        ArgumentNullException.ThrowIfNull(equipment);
        for (int slot = 0; slot < EquipmentSlots.Count; slot++)
        {
            _statSheet.SetSource(slot, equipment.Get((EquipmentSlot)slot));
        }

        DeriveFromSheet();
    }

    private void RecalculateEffectiveStats()
    {
        StatBlock baseBlock = default;
        baseBlock[StatId.Strength] = BaseStats.Strength;
        baseBlock[StatId.AttackSpeed] = BaseStats.AttackSpeed;
        baseBlock[StatId.Vitality] = BaseStats.Vitality;
        _statSheet.SetBase(baseBlock);
        DeriveFromSheet();
    }

    private void DeriveFromSheet()
    {
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
