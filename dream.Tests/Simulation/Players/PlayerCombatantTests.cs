using OpenTK.Mathematics;
using Xunit;
using Dream.Simulation.Combat;
using Dream.Simulation.Inventory;
using Dream.Simulation.Players;
using Dream.Simulation.Stats;

namespace Dream.Tests.Simulation.Players;


public class PlayerCombatantCombatStatTests
{
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
        combatant.ApplyDamage(60);

        combatant.ApplyStats(new PlayerCombatStats { Strength = 10, AttackSpeed = 2f, Vitality = 20 });

        Assert.Equal(60, combatant.Health.CurrentHealth);
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
        Assert.Equal(16, combatant.Stats.Strength);
        Assert.Equal(4.0f, combatant.Stats.AttackSpeed, 3);
        Assert.Equal(11, combatant.Stats.Vitality);
        Assert.Equal(37, combatant.DerivedStats.MinDamage);
        Assert.Equal(74, combatant.DerivedStats.MaxDamage);
        Assert.Equal(0.25f, combatant.DerivedStats.AttackIntervalSeconds, 3);
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
        Assert.Equal(3f, combatant.Stats.AttackSpeed, 3);
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
