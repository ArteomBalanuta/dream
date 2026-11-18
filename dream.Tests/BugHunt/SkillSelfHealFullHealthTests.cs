using Xunit;
using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Content;
using Dream.Simulation.Players;
using Dream.Simulation.Runtime;
using Dream.Tests.Simulation.Runtime;

namespace Dream.Tests.BugHunt;

// BH-SKILL-1: SelfHeal was the one cast path with no re-validation at effect. At full health the cast
// passed every gate, spent 15 mana and started the full 320-tick cooldown, and then RestoreHealth
// returned 0 -- a misclick on the Mend tile cost a third of a level-1 mana pool and locked the only
// heal for five seconds, repeatable at will. The runtime now refuses it before anything is spent,
// mirroring the item path's "nothing to restore". The boundary is deliberately the same as the item
// path's: only a cast that would restore strictly nothing is refused, so a partial overheal -- damaged
// by less than the heal amount -- is still a real heal and still pays for itself.
public sealed class SkillSelfHealFullHealthTests
{
    private const string Mend = "skill.mend";
    private static readonly PlayerSessionId A = new("skill-heal-a");
    private const string Character = "Alice";

    private static SkillDefinition MendSkill()
    {
        Assert.True(ContentCatalog.Shipped.TryGetSkill(Mend, out SkillDefinition skill));
        return skill;
    }

    private static ConnectedPlayer Seat(WorldSessionRuntime runtime)
    {
        Assert.True(runtime.Sessions.TryGet(A, out ConnectedPlayer? player));
        return player!;
    }

    private static AuthoritativeActionResult Cast(WorldSessionRuntime runtime, ulong sequence)
        => runtime.ProcessAuthoritativeAction(new PlayerActionMessage(A, sequence, new SkillUseIntent(Mend)));

    // A runtime with the seat attached and ten ticks run, so mana has settled before anything is measured.
    private static WorldSessionRuntime Joined(out IAuthoritativePlayerTarget target)
    {
        WorldSessionRuntime runtime = WaveIRuntimeKit.CreateRuntime();
        runtime.AttachRemoteSession(A, Character);
        ulong tick = 0;
        WaveIRuntimeKit.Tick(runtime, ref tick, 10);
        target = WaveIRuntimeKit.Target(runtime, A.Value);
        return runtime;
    }

    // The defect: at full health the cast is refused, and neither the mana nor the cooldown moves.
    [Fact]
    public void AMendAtFullHealth_IsRefused_AndSpendsNeitherManaNorCooldown()
    {
        using WorldSessionRuntime runtime = Joined(out IAuthoritativePlayerTarget target);
        Assert.Equal(target.Combatant.Health.MaxHealth, target.Combatant.Health.CurrentHealth);
        int manaBefore = target.Combatant.CurrentMana;

        AuthoritativeActionResult result = Cast(runtime, 1);

        Assert.False(result.Accepted);
        Assert.Contains("full health", result.Rejection!.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(manaBefore, target.Combatant.CurrentMana);
        Assert.True(Seat(runtime).Cooldowns.IsReady(Mend, runtime.CurrentServerTick), "a refused cast started the cooldown");
    }

    // And the refusal is not a one-off: pressing the tile again at full health still costs nothing,
    // which is the misclick the finding is about.
    [Fact]
    public void RepeatedMendsAtFullHealth_StillCostNothing()
    {
        using WorldSessionRuntime runtime = Joined(out IAuthoritativePlayerTarget target);
        int manaBefore = target.Combatant.CurrentMana;

        for (ulong sequence = 1; sequence <= 5; sequence++)
        {
            Assert.False(Cast(runtime, sequence).Accepted);
        }

        Assert.Equal(manaBefore, target.Combatant.CurrentMana);
        Assert.True(Seat(runtime).Cooldowns.IsReady(Mend, runtime.CurrentServerTick));
    }

    // Control: below full health the heal lands, spends its mana and starts its cooldown as before.
    [Fact]
    public void AMendBelowFullHealth_StillHeals_AndStillSpendsManaAndCooldown()
    {
        using WorldSessionRuntime runtime = Joined(out IAuthoritativePlayerTarget target);
        SkillDefinition mend = MendSkill();
        target.Combatant.Health.ApplyDamage(mend.HealAmount + 10);
        int healthBefore = target.Combatant.Health.CurrentHealth;
        int manaBefore = target.Combatant.CurrentMana;

        AuthoritativeActionResult result = Cast(runtime, 1);

        Assert.True(result.Accepted, result.Rejection?.Reason);
        AbilityUsedResult used = Assert.IsType<AbilityUsedResult>(result.Payload);
        Assert.Equal(mend.HealAmount, used.HealthRestored);
        Assert.Equal(healthBefore + mend.HealAmount, target.Combatant.Health.CurrentHealth);
        Assert.Equal(manaBefore - mend.ManaCost, target.Combatant.CurrentMana);
        Assert.False(Seat(runtime).Cooldowns.IsReady(Mend, runtime.CurrentServerTick));
    }

    // The boundary that must NOT move: one point of damage is still a real heal. It overheals by
    // almost the whole amount, restores only that one point, and still spends -- the refusal is for a
    // cast that would restore strictly nothing, not for an inefficient one.
    [Fact]
    public void AMendThatMostlyOverheals_IsStillAccepted_AndStillSpends()
    {
        using WorldSessionRuntime runtime = Joined(out IAuthoritativePlayerTarget target);
        SkillDefinition mend = MendSkill();
        Assert.True(mend.HealAmount > 1, "the overheal boundary needs a heal bigger than the damage");
        target.Combatant.Health.ApplyDamage(1);
        int manaBefore = target.Combatant.CurrentMana;

        AuthoritativeActionResult result = Cast(runtime, 1);

        Assert.True(result.Accepted, result.Rejection?.Reason);
        Assert.Equal(1, Assert.IsType<AbilityUsedResult>(result.Payload).HealthRestored);
        Assert.Equal(target.Combatant.Health.MaxHealth, target.Combatant.Health.CurrentHealth);
        Assert.Equal(manaBefore - mend.ManaCost, target.Combatant.CurrentMana);
        Assert.False(Seat(runtime).Cooldowns.IsReady(Mend, runtime.CurrentServerTick));
    }

    // Control: the gate is the SelfHeal's alone. An attack skill at full health is untouched by it.
    [Fact]
    public void AnAttackSkillAtFullHealth_IsUnaffected()
    {
        using WorldSessionRuntime runtime = Joined(out IAuthoritativePlayerTarget target);
        WaveIRuntimeKit.EnterCombat(runtime, A);
        int manaBefore = target.Combatant.CurrentMana;

        AuthoritativeActionResult result = runtime.ProcessAuthoritativeAction(
            new PlayerActionMessage(A, 1, new SkillUseIntent("skill.cleave")));

        Assert.True(result.Accepted, result.Rejection?.Reason);
        Assert.True(target.Combatant.CurrentMana < manaBefore, "a cleave spends mana");
        Assert.False(Seat(runtime).Cooldowns.IsReady("skill.cleave", runtime.CurrentServerTick));
    }

    // Control: a refusal at full health leaves the skill ready, so the heal is there when it is needed.
    [Fact]
    public void AfterARefusalAtFullHealth_TheHealIsStillAvailableWhenDamageLands()
    {
        using WorldSessionRuntime runtime = Joined(out IAuthoritativePlayerTarget target);
        Assert.False(Cast(runtime, 1).Accepted);

        target.Combatant.Health.ApplyDamage(20);
        AuthoritativeActionResult result = Cast(runtime, 2);

        Assert.True(result.Accepted, result.Rejection?.Reason);
        Assert.Equal(20, Assert.IsType<AbilityUsedResult>(result.Payload).HealthRestored);
    }
}
