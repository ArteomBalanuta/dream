namespace Dream.Simulation.Content;

public enum SkillKind { MeleeArc, RangedBolt, SelfHeal }

// A skill as authored in skills.json (M4). Keys are looked up case-insensitively. Cooldowns are server
// ticks. Range is in metres; ArcDegrees is the full width of a MeleeArc cone. DamagePercent scales the
// caster's normal attack damage (100 = exactly one primary attack); HealAmount is health restored by a
// SelfHeal. Fields a kind does not use are 0 (MaxTargets 1).
public sealed record SkillDefinition(
    string Key,
    string DisplayName,
    string Description,
    string IconKey,
    SkillKind Kind,
    int ManaCost,
    int CooldownTicks,
    float Range,
    float ArcDegrees,
    int MaxTargets,
    int DamagePercent,
    int HealAmount);

// The "use" object of a consumable item (M4). Restores are applied through the player's vitals, capped
// at their maximum; the cooldown is per item kind, in server ticks.
public sealed record ItemUseDefinition(int RestoreHealth, int RestoreMana, int CooldownTicks);
