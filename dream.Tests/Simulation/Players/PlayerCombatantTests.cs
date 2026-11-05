using OpenTK.Mathematics;
using Xunit;
using Dream.Simulation.Combat;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.Stats;

namespace Dream.Tests.Simulation.Players;


public class PlayerCombatantCombatStatTests
{
    // Effective attack speed is the sheet's attack speed plus AttackSpeedPerAgility per point of
    // Agility, so every expectation below is written as "the gear total plus that term" rather than as
    // the resulting decimal. An unseeded combatant has Agility 10 (PlayerCombatStats.UnseededAttribute), so the term is 0.1.
    private static float WithDefaultAgility(float sheetAttackSpeed)
        => sheetAttackSpeed + (PlayerStatRules.AttackSpeedPerAgility * PlayerCombatStats.UnseededAttribute);

    [Fact]
    public void Constructor_Seeds_Default_Derived_Stats()
    {
        PlayerCombatant combatant = new(() => Vector3.Zero);

        Assert.Equal(100, combatant.Health.MaxHealth);
        Assert.Equal(25, combatant.DerivedStats.MinDamage);
        Assert.Equal(50, combatant.DerivedStats.MaxDamage);
    }

    [Fact]
    public void ApplyStats_Preserves_Health_Ratio_When_Max_Health_Changes()
    {
        PlayerCombatant combatant = new(() => Vector3.Zero);

        // Defense 2 (the default Agility 10 / 4) is armor, so the 60 arrives as
        // 60 * 100 / 102 = 58.82 -> 59, leaving 41 of 100.
        Assert.Equal(59, combatant.ApplyDamage(60).AppliedAmount);
        Assert.Equal(41, combatant.Health.CurrentHealth);

        combatant.ApplyStats(new PlayerCombatStats { Strength = 10, AttackSpeed = 2f, Vitality = 20 });

        // The ratio is what is preserved: 41 / 100 of the new 150 is 61.5, rounded to 62.
        Assert.Equal(62, combatant.Health.CurrentHealth);
        Assert.Equal(150, combatant.Health.MaxHealth);
    }

    [Fact]
    public void ApplyEquipment_Adjusts_EffectiveStats_WithoutOverwriting_BaseStats()
    {
        PlayerCombatant combatant = new(() => Vector3.Zero);
        combatant.ApplyBaseStats(new PlayerCombatStats { Strength = 12, AttackSpeed = 2.5f, Vitality = 11 });

        EquipmentLoadout equipment = new();
        equipment.Set(
            EquipmentSlot.MainHand,
            [new StatModifier(StatId.Strength, StatOp.Flat, 4f), new StatModifier(StatId.AttackSpeed, StatOp.Flat, 1.5f)]);
        combatant.ApplyEquipment(equipment);

        Assert.Equal(12, combatant.BaseStats.Strength);
        Assert.Equal(2.5f, combatant.BaseStats.AttackSpeed, 3);
        Assert.Equal(11, combatant.BaseStats.Vitality);
        // Base 2.5 plus the 1.5 on the weapon is 4.0 on the sheet, plus 0.1 from Agility 10: 4.1.
        float expectedSpeed = WithDefaultAgility(2.5f + 1.5f);
        Assert.Equal(16, combatant.Stats.Strength);
        Assert.Equal(expectedSpeed, combatant.Stats.AttackSpeed, 3);
        Assert.Equal(11, combatant.Stats.Vitality);
        Assert.Equal(37, combatant.DerivedStats.MinDamage);
        Assert.Equal(74, combatant.DerivedStats.MaxDamage);
        // One attack per 1 / 4.1 s, about 0.244.
        Assert.Equal(1f / expectedSpeed, combatant.DerivedStats.AttackIntervalSeconds, 3);
    }

    [Fact]
    public void ApplyEquipment_SumsAllFourSlots_AndReadsArmor()
    {
        PlayerCombatant combatant = new(() => Vector3.Zero);
        combatant.ApplyBaseStats(new PlayerCombatStats { Strength = 10, AttackSpeed = 2f, Vitality = 10 });
        Assert.Equal(0, combatant.EffectiveArmor);

        EquipmentLoadout equipment = new();
        equipment.Set(EquipmentSlot.MainHand, [new StatModifier(StatId.Strength, StatOp.Flat, 4f), new StatModifier(StatId.AttackSpeed, StatOp.Flat, 1f)]);
        equipment.Set(EquipmentSlot.OffHand, [new StatModifier(StatId.Strength, StatOp.Flat, 1f), new StatModifier(StatId.Armor, StatOp.Flat, 3f)]);
        equipment.Set(EquipmentSlot.Head, [new StatModifier(StatId.Armor, StatOp.Flat, 5f), new StatModifier(StatId.Vitality, StatOp.Flat, 2f)]);
        equipment.Set(EquipmentSlot.Body, [new StatModifier(StatId.Armor, StatOp.Flat, 8f), new StatModifier(StatId.Vitality, StatOp.Flat, 3f)]);
        combatant.ApplyEquipment(equipment);

        Assert.Equal(15, combatant.Stats.Strength);
        // Base 2 plus the 1 on the weapon is 3 on the sheet, plus 0.1 from Agility 10: 3.1.
        Assert.Equal(WithDefaultAgility(2f + 1f), combatant.Stats.AttackSpeed, 3);
        Assert.Equal(15, combatant.Stats.Vitality);
        Assert.Equal(16, combatant.EffectiveArmor);
        // The base stats and the base of Armor are untouched.
        Assert.Equal(10, combatant.BaseStats.Strength);
        Assert.Equal(0f, combatant.StatSheet.Base[StatId.Armor]);

        equipment.Clear();
        combatant.ApplyEquipment(equipment);

        Assert.Equal(10, combatant.Stats.Strength);
        Assert.Equal(0, combatant.EffectiveArmor);
    }

    [Fact]
    public void ApplyEquipment_RescalesHealthOnce_ByTheFinalMaximum()
    {
        PlayerCombatant combatant = new(() => Vector3.Zero);
        int baseMax = combatant.Health.MaxHealth;
        combatant.Health.SetAuthoritativeState(baseMax / 2, baseMax);
        EquipmentLoadout equipment = new();
        equipment.Set(EquipmentSlot.Head, [new StatModifier(StatId.Vitality, StatOp.Flat, 2f)]);
        equipment.Set(EquipmentSlot.Body, [new StatModifier(StatId.Vitality, StatOp.Flat, 3f)]);

        combatant.ApplyEquipment(equipment);

        Assert.True(combatant.Health.MaxHealth > baseMax);
        Assert.Equal((double)combatant.Health.MaxHealth / 2, combatant.Health.CurrentHealth, 1.0);
    }

    [Fact]
    public void TryStartAttack_Uses_Cooldown_Gating()
    {
        PlayerCombatant combatant = new(() => Vector3.Zero);

        Assert.True(combatant.TryStartAttack());
        Assert.False(combatant.TryStartAttack());

        combatant.AdvanceCombat(0.5f);

        Assert.True(combatant.TryStartAttack());
    }

    [Fact]
    public void ResolveAttack_Forces_Critical_When_Roll_Is_Within_Crit_Chance()
    {
        PlayerCombatant combatant = new(() => Vector3.Zero);

        PlayerAttackResolution attack = combatant.ResolveAttack(() => 25, () => 0.10);

        Assert.True(attack.IsCritical);
        Assert.Equal(75, attack.Damage);
        Assert.Equal(FloatingCombatTextKind.Critical, attack.FloatingTextKind);
    }

    [Fact]
    public void ResolveAttack_Uses_Damage_Roll_For_Non_Crit()
    {
        PlayerCombatant combatant = new(() => Vector3.Zero);

        PlayerAttackResolution attack = combatant.ResolveAttack(() => 31, () => 0.90);

        Assert.False(attack.IsCritical);
        Assert.Equal(31, attack.Damage);
        Assert.Equal(FloatingCombatTextKind.Regular, attack.FloatingTextKind);
    }
}
