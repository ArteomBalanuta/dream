using Dream.Simulation.Players;
using Dream.Simulation.Stats;
using Xunit;

namespace Dream.Tests.Simulation.Stats;

// Wave F fix loop: a source with a NaN or infinite modifier would poison the resolved stats.
public sealed class StatSheetNonFiniteSourceTests
{
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void SetSource_RejectsANonFiniteValue_AndLeavesTheSheetUnchanged(float value)
    {
        StatSheet sheet = new();
        StatModifier[] valid = [new StatModifier(StatId.Strength, StatOp.Flat, 2f)];
        sheet.SetSource(0, valid);
        float before = sheet.Effective[StatId.Strength];

        Assert.Throws<ArgumentException>(
            () => sheet.SetSource(0, [new StatModifier(StatId.Strength, StatOp.Flat, value)]));

        Assert.Equal(before, sheet.Effective[StatId.Strength]);
    }

    [Fact]
    public void StatRules_ShareThePlayerCombatStatsLimits()
    {
        Assert.Equal(PlayerCombatStats.MinStrength, StatRules.MinBase(StatId.Strength));
        Assert.Equal(PlayerCombatStats.MaxStrength, StatRules.MaxBase(StatId.Strength));
        Assert.Equal(PlayerCombatStats.MinVitality, StatRules.MinBase(StatId.Vitality));
        Assert.Equal(PlayerCombatStats.MaxVitality, StatRules.MaxBase(StatId.Vitality));
        Assert.Equal(PlayerCombatStats.MinAttackSpeed, StatRules.MinBase(StatId.AttackSpeed));
        Assert.Equal(PlayerCombatStats.MaxAttackSpeed, StatRules.MaxBase(StatId.AttackSpeed));
    }
}
