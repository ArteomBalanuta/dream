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

    // The Armor the equipment adds up to (base 0, effective range 0..1000). Damage taken is
    // mitigated by it (MitigateDamage); Armor 0 takes every hit unchanged.
    public int EffectiveArmor => (int)_statSheet.Effective[StatId.Armor];

    // Share of incoming damage taken, from player.damage_taken_factor. Cached: it is evaluated only
    // when the effective Armor differs from the armor it was last evaluated for (never per hit, and not
    // on the client's per-snapshot ApplyBaseStats). 1.0 at Armor 0.
    public double DamageTakenFactor { get; private set; } = 1.0;

    private int _factorArmor;

    public PlayerCombatDerivedStats DerivedStats { get; private set; }

    // Defense (Agility / 4) adds to the armor in damage mitigation.
    public int Defense { get; private set; }

    public int MagicMinDamage { get; private set; }

    public int MagicMaxDamage { get; private set; }

    public int Level { get; private set; } = 1;

    public int Experience { get; private set; }

    public HealthState Health { get; }

    public CombatActorHandle Actor { get; }

    // M4: mana. The maximum comes from the level and the effective Energy (PlayerManaRules); the current value starts full,
    // is clamped, never topped up, when the maximum falls, and is not part of AdvanceCombat (which the
    // client runs too). Regeneration and persistence belong to the session runtime.
    public int CurrentMana { get; private set; }

    public int MaxMana { get; private set; }

    // Spends amount when that much is available; a negative amount is refused. Nothing changes on false.
    public bool TrySpendMana(int amount)
    {
        if (amount < 0 || amount > CurrentMana)
        {
            return false;
        }

        CurrentMana -= amount;
        return true;
    }

    // Returns the mana actually added (0 for an amount of 0 or less, or when already full).
    public int RestoreMana(int amount)
    {
        if (amount <= 0)
        {
            return 0;
        }

        int applied = Math.Min(amount, MaxMana - CurrentMana);
        CurrentMana += applied;
        return applied;
    }

    // Returns the health actually added: 0 for an amount of 0 or less, for a dead player and when full.
    public int RestoreHealth(int amount)
    {
        if (amount <= 0 || !Health.IsAlive)
        {
            return 0;
        }

        int applied = Math.Min(amount, Health.MaxHealth - Health.CurrentHealth);
        if (applied > 0)
        {
            Health.SetAuthoritativeState(Health.CurrentHealth + applied, Health.MaxHealth);
        }

        return applied;
    }

    public void SetMana(int current)
    {
        CurrentMana = Math.Clamp(current, 0, MaxMana);
    }

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
        BaseStats.Agility = (stats ?? new PlayerCombatStats()).Agility;
        BaseStats.Energy = (stats ?? new PlayerCombatStats()).Energy;
        RecalculateEffectiveStats();
        Health = new HealthState(DerivedStats.MaxHealth);
        Actor = new CombatActorHandle(actorId, CreatureKind.Player, Health, this);
    }

    public DamageReceipt ApplyDamage(int amount)
    {
        DamageReceipt receipt = Health.ApplyDamage(MitigateDamage(amount));
        if (receipt.WasFatal)
        {
            _eventHub?.Publish(new ActorDiedEvent(Actor));
        }

        return receipt;
    }

    // Pure and allocation free. Armor 0 (or an amount of 0 or less) returns the amount unchanged; otherwise
    // the amount scaled by the cached factor, rounded away from zero, and at least 1.
    public int MitigateDamage(int amount)
    {
        if (amount <= 0 || _factorArmor <= 0)
        {
            return amount;
        }

        return Math.Max(1, (int)Math.Round(amount * DamageTakenFactor, MidpointRounding.AwayFromZero));
    }

    public void AdvanceCombat(float deltaTime)
    {
        _attackCooldownRemaining = Math.Max(0f, _attackCooldownRemaining - Math.Max(0f, deltaTime));
    }

    // The base block with the pool values of this moment, as it goes on the wire.
    public PlayerCombatStatsSnapshot CaptureStatsSnapshot()
    {
        return PlayerCombatStatsSnapshot.From(BaseStats);
    }

    public void ApplyBaseStats(PlayerCombatStats updatedStats)
    {
        BaseStats.Strength = updatedStats.Strength;
        BaseStats.AttackSpeed = updatedStats.AttackSpeed;
        BaseStats.Vitality = updatedStats.Vitality;
        BaseStats.Agility = updatedStats.Agility;
        BaseStats.Energy = updatedStats.Energy;

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

    // Sets every equipment source (all EquipmentSlots.Count slots; source slot = EquipmentSlot value,
    // resolved in ascending order), then derives once, so max health is rescaled a
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
        baseBlock[StatId.Agility] = BaseStats.Agility;
        baseBlock[StatId.Energy] = BaseStats.Energy;
        _statSheet.SetBase(baseBlock);
        DeriveFromSheet();
    }

    private void DeriveFromSheet()
    {
        ref readonly StatBlock effective = ref _statSheet.Effective;
        Stats.Strength = (int)effective[StatId.Strength];
        Stats.Vitality = (int)effective[StatId.Vitality];
        Stats.Agility = (int)effective[StatId.Agility];
        Stats.Energy = (int)effective[StatId.Energy];
        Stats.AttackSpeed = PlayerStatRules.EffectiveAttackSpeed(effective[StatId.AttackSpeed], Stats.Agility);
        DerivedStats = Stats.Derive(_combatFormulaService, Level);
        Defense = PlayerStatRules.Defense(Stats.Agility);
        MagicMinDamage = PlayerStatRules.MagicMinDamage(Stats.Energy);
        MagicMaxDamage = PlayerStatRules.MagicMaxDamage(Stats.Energy);
        int armor = EffectiveArmor + Defense;
        if (armor != _factorArmor)
        {
            _factorArmor = armor;
            DamageTakenFactor = _combatFormulaService.EvaluateDamageTakenFactor(armor);
        }

        int maxMana = PlayerManaRules.MaxMana(Level, Stats.Energy);
        CurrentMana = MaxMana == 0 ? maxMana : Math.Min(CurrentMana, maxMana);
        MaxMana = maxMana;

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
