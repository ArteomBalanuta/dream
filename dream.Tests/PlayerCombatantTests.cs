using OpenTK.Mathematics;
using Xunit;

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
