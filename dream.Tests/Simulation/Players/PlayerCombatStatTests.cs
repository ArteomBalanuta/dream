using Xunit;
using Dream.Server.Persistence;
using Dream.Server.Rules;
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

    [Fact]
    public void Derive_With_CustomFormulaService_UsesConfiguredDbLikeExpressions()
    {
        PlayerCombatStats stats = new() { Strength = 10, AttackSpeed = 2f, Vitality = 10 };
        CombatFormulaService service = CombatFormulaService.CreateForDefinitions(
            new[]
            {
                new CombatFormulaDefinition("player.min_damage", "10 + strength", "test", true, DateTime.UtcNow),
                new CombatFormulaDefinition("player.max_damage", "20 + strength * 2", "test", true, DateTime.UtcNow),
                new CombatFormulaDefinition("player.max_health", "10 + vitality * 10", "test", true, DateTime.UtcNow),
                new CombatFormulaDefinition("player.critical_damage", "max_damage * 2", "test", true, DateTime.UtcNow),
                new CombatFormulaDefinition("player.critical_chance", "0.25", "test", true, DateTime.UtcNow),
                new CombatFormulaDefinition("player.attack_interval_seconds", "2.0 / attack_speed", "test", true, DateTime.UtcNow),
                new CombatFormulaDefinition("player.experience_to_next_level", "1000 + level * 23", "test", true, DateTime.UtcNow),
                new CombatFormulaDefinition("enemy.experience_reward", "enemy_level * 14", "test", true, DateTime.UtcNow)
            });

        PlayerCombatDerivedStats derived = stats.Derive(service, level: 1);

        Assert.Equal(20, derived.MinDamage);
        Assert.Equal(40, derived.MaxDamage);
        Assert.Equal(80, derived.CriticalDamage);
        Assert.Equal(110, derived.MaxHealth);
        Assert.Equal(0.25f, derived.CriticalChance, 3);
        Assert.Equal(1.0f, derived.AttackIntervalSeconds, 3);
    }
}
