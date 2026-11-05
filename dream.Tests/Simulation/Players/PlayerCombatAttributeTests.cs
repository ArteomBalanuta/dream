using OpenTK.Mathematics;
using Xunit;
using Dream.Simulation.Players;
using Dream.Simulation.Stats;

namespace Dream.Tests.Simulation.Players;

// Agility, Energy and the mana pool on the combatant; the point budget is PlayerStatRules (derived from
// the level, never stored), and a spend goes through PlayerStatRules.TryValidateSpend.
public class PlayerCombatAttributeTests
{
    private static PlayerCombatant Combatant(int agility = 0, int energy = 0) =>
        new(() => Vector3.Zero, stats: new PlayerCombatStats { Agility = agility, Energy = energy });

    [Fact]
    public void Rules_AreFivePointsPerLevel_AndTheDocumentedFormulas()
    {
        Assert.Equal(0, PlayerStatRules.PointsEarned(1));
        Assert.Equal(15, PlayerStatRules.PointsEarned(4));
        Assert.Equal(2.4f, PlayerStatRules.EffectiveAttackSpeed(2f, 40), 3);
        Assert.Equal(5f, PlayerStatRules.EffectiveAttackSpeed(4.9f, 400), 3);
        Assert.Equal(10, PlayerStatRules.Defense(40));
        Assert.Equal(100, PlayerManaRules.MaxMana(1, 10));
        Assert.Equal(115, PlayerManaRules.MaxMana(4, 10));
        Assert.Equal(10, PlayerStatRules.MagicMinDamage(10));
        Assert.Equal(20, PlayerStatRules.MagicMaxDamage(10));
    }

    [Fact]
    public void ASpend_OnStrengthAndAgility_RaisesThem_AndUsesThePoints()
    {
        // The point count measures from the start (PlayerStatRules.Start*, 10), so the character must
        // be seeded there: a sheet below the start has spent nothing and a spend of 1 would cost 0 of it.
        PlayerCombatant combatant = new(() => Vector3.Zero, stats: new PlayerCombatStats
        {
            Strength = PlayerStatRules.StartStrength,
            Vitality = PlayerStatRules.StartVitality,
            Agility = PlayerStatRules.StartAgility,
            Energy = PlayerStatRules.StartEnergy
        });
        combatant.ApplyProgression(2, 0);
        Assert.Equal(5, PlayerStatRules.PointsAvailable(combatant.Level, combatant.BaseStats));

        Assert.True(PlayerStatRules.TryValidateSpend(combatant.Level, PlayerCombatStatsSnapshot.From(combatant.BaseStats), StatId.Strength, 1, out PlayerCombatStatsSnapshot afterStrength, out string? reason));
        Assert.Null(reason);
        combatant.ApplyBaseStats(afterStrength.ToCombatStats());
        Assert.True(PlayerStatRules.TryValidateSpend(combatant.Level, PlayerCombatStatsSnapshot.From(combatant.BaseStats), StatId.Agility, 2, out PlayerCombatStatsSnapshot afterAgility, out _));
        combatant.ApplyBaseStats(afterAgility.ToCombatStats());

        Assert.Equal(PlayerStatRules.StartStrength + 1, combatant.BaseStats.Strength);
        Assert.Equal(PlayerStatRules.StartAgility + 2, combatant.BaseStats.Agility);
        // Level 2 earns 5; 1 + 2 spent leaves 2.
        Assert.Equal(2, PlayerStatRules.PointsAvailable(combatant.Level, combatant.BaseStats));
        Assert.Equal(PlayerStatRules.StartStrength + 1, combatant.Stats.Strength);
    }

    [Theory]
    [InlineData(StatId.AttackSpeed, 1, 2)]
    [InlineData(StatId.Armor, 1, 2)]
    [InlineData(StatId.Strength, 0, 2)]
    [InlineData(StatId.Strength, -1, 2)]
    [InlineData(StatId.Strength, 6, 2)]
    [InlineData(StatId.Vitality, 1, 1)]
    public void ASpend_IsRefused_AndChangesNothing(StatId stat, int points, int level)
    {
        PlayerCombatStatsSnapshot start = new(10, 2f, 10, 10, 10);

        Assert.False(PlayerStatRules.TryValidateSpend(level, start, stat, points, out PlayerCombatStatsSnapshot after, out string? reason));

        Assert.False(string.IsNullOrEmpty(reason));
        Assert.Equal(start, after);
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

        // 50 at level 1 plus 5 per Energy.
        Assert.Equal(100, combatant.MaxMana);
        Assert.Equal(100, combatant.CurrentMana);
        Assert.Equal(10, combatant.MagicMinDamage);
        Assert.Equal(20, combatant.MagicMaxDamage);
    }

    [Fact]
    public void SpendingOnEnergy_RaisesTheCap_ButNotTheCurrentMana()
    {
        PlayerCombatant combatant = Combatant(energy: 10);
        combatant.ApplyProgression(2, 0);
        combatant.SetMana(combatant.MaxMana);
        Assert.Equal(105, combatant.MaxMana);

        Assert.True(PlayerStatRules.TryValidateSpend(combatant.Level, PlayerCombatStatsSnapshot.From(combatant.BaseStats), StatId.Energy, 2, out PlayerCombatStatsSnapshot after, out _));
        combatant.ApplyBaseStats(after.ToCombatStats());

        Assert.Equal(115, combatant.MaxMana);
        Assert.Equal(105, combatant.CurrentMana);
    }

    [Fact]
    public void AdvanceCombat_DoesNotRegenerate_AndRestoreManaFillsTheGap()
    {
        // Regeneration belongs to the session runtime (PlayerManaRules), not to AdvanceCombat.
        PlayerCombatant combatant = Combatant(energy: 10);
        combatant.SetMana(10);
        Assert.Equal(10, combatant.CurrentMana);

        for (int i = 0; i < (64 * 3) + 1; i++)
        {
            combatant.AdvanceCombat(1f / 64f);
        }

        Assert.Equal(10, combatant.CurrentMana);
        Assert.Equal(30, combatant.RestoreMana(30));
        Assert.Equal(40, combatant.CurrentMana);
        Assert.Equal(60, combatant.RestoreMana(100));
        Assert.Equal(100, combatant.CurrentMana);
        Assert.Equal(0, combatant.RestoreMana(1));
        Assert.Equal(0, combatant.RestoreMana(-5));
    }

    [Fact]
    public void LegacyAttackSpeedSteps_StillCountAsSpent()
    {
        // 2.25 attack speed is five old 0.05 steps; every attribute at the start (1000) costs nothing.
        const int s = PlayerStatRules.StartStrength;
        const int v = PlayerStatRules.StartVitality;
        const int a = PlayerStatRules.StartAgility;
        const int e = PlayerStatRules.StartEnergy;
        Assert.Equal(5, PlayerStatRules.PointsSpent(s, 2.25f, v, a, e));
        Assert.Equal(0, PlayerStatRules.PointsAvailable(2, s, 2.25f, v, a, e));
        // Agility 2 and Energy 1 above the start cost 3.
        Assert.Equal(3, PlayerStatRules.PointsSpent(s, 2f, v, a + 2, e + 1));
    }
}
