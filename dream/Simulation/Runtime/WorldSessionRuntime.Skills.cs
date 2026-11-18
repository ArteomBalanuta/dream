using Dream.Multiplayer.Protocol;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Content;
using Dream.Simulation.Players;
using Dream.Simulation.World;
using OpenTK.Mathematics;

namespace Dream.Simulation.Runtime;

// Skills (M4 workstream B): validates a cast, spends mana and starts the cooldown, then hands the
// resolution to AuthoritativeCombatResolver. Nothing is spent on a rejection.
public sealed partial class WorldSessionRuntime
{
    private AuthoritativeActionResult ProcessSkillUseAction(
        PlayerActionMessage message,
        AuthoritativeSessionActionContext context,
        SkillUseIntent intent)
    {
        if (!ContentCatalog.Shipped.TryGetSkill(intent.SkillKey ?? string.Empty, out SkillDefinition? skill)
            || skill == null)
        {
            return CreateRejectedActionResult(message, "Skill rejected: unknown skill.");
        }

        bool isHeal = skill.Kind == SkillKind.SelfHeal;
        if (!isHeal
            && (!context.SupportsCombatActions || context.InteractionMode != PlayerInteractionMode.Combat))
        {
            return CreateRejectedActionResult(message, "Skill rejected: switch to Combat mode (G).");
        }

        // The origin is always the server's; a client aim is only a direction.
        Vector3 direction = context.CombatRayDirection;
        if (intent.AimDirection is Vector3 aim)
        {
            if (!float.IsFinite(aim.X) || !float.IsFinite(aim.Y) || !float.IsFinite(aim.Z)
                || !float.IsFinite(aim.LengthSquared)
                || aim.LengthSquared <= 0.0001f)
            {
                return CreateRejectedActionResult(message, "Skill rejected: invalid aim direction.");
            }

            direction = aim;
        }

        ulong tick = CurrentServerTick;
        ConnectedPlayer player = context.Player;
        if (!player.Cooldowns.IsReady(skill.Key, tick))
        {
            return CreateRejectedActionResult(message, "Skill rejected: on cooldown.");
        }

        // BH-SKILL-1: a cast that cannot do anything its kind promises must not take the mana or the
        // cooldown. A SelfHeal at full health restored 0 and still charged both, so a misclick on the
        // Mend tile cost 15 mana and locked the only heal for five seconds -- exactly when the player
        // then took a hit. The item path already encodes this invariant ("nothing to restore",
        // Consumables.cs), and the boundary is the same one it uses: only a cast that would restore
        // strictly nothing is refused, so a partial overheal is still a real heal and still spends.
        if (isHeal && context.Combatant.Health.CurrentHealth >= context.Combatant.Health.MaxHealth)
        {
            return CreateRejectedActionResult(message, "Skill rejected: already at full health.");
        }

        if (!context.Combatant.TrySpendMana(skill.ManaCost))
        {
            return CreateRejectedActionResult(message, "Skill rejected: not enough mana.");
        }

        // BH-SKILL-2: recorded against the character too, so a reconnect resumes it instead of wiping it.
        StartCooldown(player, skill.Key, tick + (ulong)Math.Max(0, skill.CooldownTicks), tick);
        player.VitalsDirty = true;
        if (!isHeal)
        {
            // Attacking ends spawn protection (the CombatUseIntent line in ProcessAction covers only that intent).
            // BH-PVP-1: spent on the character too, so a reconnect cannot hand the window back mid-fight.
            SpendSpawnProtection(player);
        }

        AuthoritativeActionResult result = _combatResolver.ProcessSkillUse(
            message,
            skill,
            context.CombatRayOrigin,
            direction,
            context.Combatant,
            tick);

        // Told to every peer so they can draw it; the caster already draws its own from this result.
        // Queued strictly on acceptance, after every refusal above has returned.
        if (result.Accepted)
        {
            QueueCastEvent(new CastEventDelta(
                context.Combatant.Actor.Id,
                skill.Kind,
                context.CombatRayOrigin,
                direction,
                skill.Range));
        }

        return result;
    }
}
