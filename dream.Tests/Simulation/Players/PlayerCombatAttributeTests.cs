using OpenTK.Mathematics;
using Xunit;
using Dream.Simulation.Players;
using Dream.Simulation.Stats;

namespace Dream.Tests.Simulation.Players;

// Attribute points, Agility, Energy and the mana pool.
public class PlayerCombatAttributeTests
{
    private static PlayerCombatant Combatant(int agility = 0, int energy = 0, int points = 0) =>
        new(() => Vector3.Zero, stats: new PlayerCombatStats { Agility = agility, Energy = energy, StatPoints = points });

    [Fact]
    public void Rules_AreFivePointsPerLevel_AndTheDocumentedFormulas()
    {
        Assert.Equal(0, PlayerAttributeRules.TotalPointsForLevel(1));
        Assert.Equal(15, PlayerAttributeRules.TotalPointsForLevel(4));
        Assert.Equal(2.4f, PlayerAttributeRules.EffectiveAttackSpeed(2f, 40), 3);
        Assert.Equal(5f, PlayerAttributeRules.EffectiveAttackSpeed(4.9f, 400), 3);
        Assert.Equal(10, PlayerAttributeRules.Defense(40));
        Assert.Equal(70, PlayerAttributeRules.MaxMana(10));
        Assert.Equal(10, PlayerAttributeRules.MagicMinDamage(10));
        Assert.Equal(20, PlayerAttributeRules.MagicMaxDamage(10));
    }

    [Fact]
    public void TrySpendPoint_RaisesTheAttribute_AndSpendsThePoint()
    {
        PlayerCombatant combatant = Combatant(points: 3);

        Assert.True(combatant.TrySpendPoint(StatId.Strength, 1, out string? reason));
        Assert.Null(reason);
        Assert.True(combatant.TrySpendPoint(StatId.Agility, 2, out _));

        Assert.Equal(11, combatant.BaseStats.Strength);
        Assert.Equal(2, combatant.BaseStats.Agility);
        Assert.Equal(0, combatant.StatPoints);
        Assert.Equal(11, combatant.Stats.Strength);
        Assert.Equal(27, combatant.DerivedStats.MinDamage);
    }

    [Theory]
    [InlineData(StatId.AttackSpeed, 1, 5)]
    [InlineData(StatId.Armor, 1, 5)]
    [InlineData(StatId.Strength, 0, 5)]
    [InlineData(StatId.Strength, -1, 5)]
    [InlineData(StatId.Strength, 6, 5)]
    [InlineData(StatId.Vitality, 1, 0)]
    public void TrySpendPoint_Refuses_AndChangesNothing(StatId attribute, int points, int available)
    {
        PlayerCombatant combatant = Combatant(points: available);

        Assert.False(combatant.TrySpendPoint(attribute, points, out string? reason));

        Assert.False(string.IsNullOrEmpty(reason));
        Assert.Equal(available, combatant.StatPoints);
        Assert.Equal(10, combatant.BaseStats.Strength);
        Assert.Equal(10, combatant.BaseStats.Vitality);
    }

    [Fact]
    public void Agility_RaisesAttackSpeed_AndDefenseMitigatesDamage()
    {
        PlayerCombatant plain = Combatant();
        PlayerCombatant agile = Combatant(agility: 100);

        Assert.Equal(2f, plain.Stats.AttackSpeed, 3);
        Assert.Equal(3f, agile.Stats.AttackSpeed, 3);
        Assert.Equal(25, agile.Defense);
        Assert.Equal(40, plain.MitigateDamage(40));
        Assert.Equal(32, agile.MitigateDamage(40)); // 100 / (100 + 25) = 0.8
    }

    [Fact]
    public void Energy_SizesTheManaPool_AndDerivesMagicDamage()
    {
        PlayerCombatant combatant = Combatant(energy: 10);

        Assert.Equal(70, combatant.MaxMana);
        Assert.Equal(70, combatant.Mana);
        Assert.Equal(10, combatant.MagicMinDamage);
        Assert.Equal(20, combatant.MagicMaxDamage);
        Assert.Equal(70, combatant.Stats.MaxMana);
        Assert.Equal(70, combatant.Stats.Mana);
    }

    [Fact]
    public void SpendingOnEnergy_RaisesTheCap_ButNotTheCurrentMana()
    {
        PlayerCombatant combatant = Combatant(points: 2);
        Assert.Equal(20, combatant.MaxMana);

        Assert.True(combatant.TrySpendPoint(StatId.Energy, 2, out _));

        Assert.Equal(30, combatant.MaxMana);
        Assert.Equal(20, combatant.Mana);
    }

    [Fact]
    public void ManaRegenerates_OnePerSecond_AndRestoreManaFillsTheGap()
    {
        PlayerCombatant combatant = Combatant(energy: 10);
        combatant.ApplyBaseStats(new PlayerCombatStats { Energy = 10, Mana = 10, MaxMana = 70 });
        Assert.Equal(10, combatant.Mana);

        for (int i = 0; i < (64 * 3) + 1; i++)
        {
            combatant.AdvanceCombat(1f / 64f);
        }

        Assert.Equal(13, combatant.Mana);
        Assert.Equal(30, combatant.RestoreMana(30));
        Assert.Equal(43, combatant.Mana);
        Assert.Equal(27, combatant.RestoreMana(100));
        Assert.Equal(70, combatant.Mana);
        Assert.Equal(0, combatant.RestoreMana(1));
        Assert.Equal(0, combatant.RestoreMana(-5));
    }

    [Fact]
    public void GrantStatPoints_AddsToTheUnspentPoints()
    {
        PlayerCombatant combatant = Combatant(points: 2);

        combatant.GrantStatPoints(10);
        combatant.GrantStatPoints(-4);
        combatant.GrantStatPoints(0);

        Assert.Equal(12, combatant.StatPoints);
        Assert.Equal(12, combatant.CaptureStatsSnapshot().StatPoints);
    }

    [Fact]
    public void CaptureStatsSnapshot_CarriesTheBaseBlockAndThePool()
    {
        PlayerCombatant combatant = Combatant(agility: 8, energy: 6, points: 4);

        PlayerCombatStatsSnapshot snapshot = combatant.CaptureStatsSnapshot();

        Assert.Equal(new PlayerCombatStatsSnapshot(10, 2f, 10, 8, 6, 4, 50, 50), snapshot);
    }
}
