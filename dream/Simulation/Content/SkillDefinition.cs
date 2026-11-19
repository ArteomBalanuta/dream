namespace Dream.Simulation.Content;

public enum SkillKind { MeleeArc, RangedBolt, SelfHeal }

// A skill as authored in skills.json (M4). Keys are looked up case-insensitively. Cooldowns are server
// ticks. Range is in metres; ArcDegrees is the full width of a MeleeArc cone. DamagePercent scales the
// caster's normal attack damage (100 = exactly one primary attack); HealAmount is health restored by a
// SelfHeal. Fields a kind does not use are 0 (MaxTargets 1). A MeleeArc that omits maxTargets hits every
// target in its arc: MaxTargets is then AllTargets.
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
    int HealAmount)
{
    // MaxTargets of a skill with no cap on how many targets it hits.
    public const int AllTargets = int.MaxValue;
}

// The "use" object of a consumable item (M4). Restores are applied through the player's vitals, capped
// at their maximum; the cooldown is shared by a cooldown group, in server ticks. The group is the optional
// "cooldownGroup" of items.json; without it every health potion shares "potion.health" and every mana
// potion "potion.mana", so drinking different kinds of one pool cannot chain past the cooldown.
public sealed record ItemUseDefinition(int RestoreHealth, int RestoreMana, int CooldownTicks, string? CooldownGroup = null)
{
    public const string HealthGroup = "potion.health";
    public const string ManaGroup = "potion.mana";

    public string EffectiveCooldownGroup => !string.IsNullOrEmpty(CooldownGroup)
        ? CooldownGroup
        : RestoreHealth > 0 ? HealthGroup : ManaGroup;
}
