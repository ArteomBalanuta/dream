using Xunit;
using Dream.Simulation.Combat;
using Dream.Simulation.Content;
using Dream.Simulation.Runtime;

namespace Dream.Tests.Simulation.Combat;

// The swing timeline is derived, never restated: the totals and the second-valued constants are the
// tick counts over the authoritative rate, and every shipped MeleeArc is timed by them.
public sealed class MeleeArcSwingContentTests
{
    [Fact]
    public void TheTimelineIsWindupThenActiveThenRecovery_AndItsSecondsAreTheTicksOverTheClockRate()
    {
        Assert.Equal(MeleeArcSwing.WindupTicks + MeleeArcSwing.ActiveTicks + MeleeArcSwing.RecoveryTicks, MeleeArcSwing.TotalTicks);
        double rate = SimulationClock.AuthoritativeTicksPerSecond;
        Assert.Equal(MeleeArcSwing.WindupSeconds, MeleeArcSwing.WindupTicks / rate, 5);
        Assert.Equal(MeleeArcSwing.ActiveSeconds, MeleeArcSwing.ActiveTicks / rate, 5);
        Assert.Equal(MeleeArcSwing.ActiveEndSeconds, (MeleeArcSwing.WindupTicks + MeleeArcSwing.ActiveTicks) / rate, 5);
        Assert.Equal(MeleeArcSwing.TotalSeconds, MeleeArcSwing.TotalTicks / rate, 5);
        Assert.True(MeleeArcSwing.WindupTicks > 0 && MeleeArcSwing.ActiveTicks > 0 && MeleeArcSwing.RecoveryTicks > 0);
    }

    // A swing that outlasted its own cooldown would let a second cast overlap the first.
    [Fact]
    public void EveryShippedMeleeArc_IsReadyAgainOnlyAfterItsSwingEnds()
    {
        SkillDefinition[] arcs = ContentCatalog.Shipped.Skills.Where(s => s.Kind == SkillKind.MeleeArc).ToArray();
        Assert.NotEmpty(arcs);
        Assert.All(arcs, arc => Assert.True(
            arc.CooldownTicks >= MeleeArcSwing.TotalTicks,
            $"{arc.Key} cools down in {arc.CooldownTicks} ticks, before its {MeleeArcSwing.TotalTicks}-tick swing ends"));
    }

    [Fact]
    public void TheShippedCleave_CostsFiveMana_AndHitsEveryTarget()
    {
        Assert.True(ContentCatalog.Shipped.TryGetSkill("skill.cleave", out SkillDefinition? cleave));
        Assert.Equal(SkillKind.MeleeArc, cleave!.Kind);
        Assert.Equal(5, cleave.ManaCost);
        Assert.Equal(SkillDefinition.AllTargets, cleave.MaxTargets);
    }
}
