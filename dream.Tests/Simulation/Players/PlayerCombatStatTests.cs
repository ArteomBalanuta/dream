using Xunit;
using Dream.Simulation.Players;

namespace Dream.Tests.Simulation.Players;


public class PlayerCombatStatTests
{
    [Fact]
    public void Derive_Default_Stats_Returns_Expected_Combat_Profile()
    {
        PlayerCombatStats stats = new();

        PlayerCombatDerivedStats derived = stats.Derive();

        Assert.Equal(25, derived.MinDamage);
        Assert.Equal(50, derived.MaxDamage);
        Assert.Equal(75, derived.CriticalDamage);
        Assert.Equal(100, derived.MaxHealth);
        Assert.Equal(0.5f, derived.AttackIntervalSeconds, 3);
    }

    [Fact]
    public void Derive_Uses_Strength_To_Scale_Damage_Range()
    {
        PlayerCombatStats stats = new() { Strength = 12 };

        PlayerCombatDerivedStats derived = stats.Derive();

        Assert.Equal(29, derived.MinDamage);
        Assert.Equal(58, derived.MaxDamage);
    }

    [Fact]
    public void Derive_Uses_Vitality_To_Scale_Max_Health()
    {
        PlayerCombatStats stats = new() { Vitality = 16 };

        PlayerCombatDerivedStats derived = stats.Derive();

        Assert.Equal(130, derived.MaxHealth);
    }

    [Fact]
    public void Derive_Uses_Attack_Speed_To_Scale_Attack_Interval()
    {
        PlayerCombatStats stats = new() { AttackSpeed = 4f };

        PlayerCombatDerivedStats derived = stats.Derive();

        Assert.Equal(0.25f, derived.AttackIntervalSeconds, 3);
    }
}
