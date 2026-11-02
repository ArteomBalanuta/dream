namespace Dream.Multiplayer.Protocol;

// The accepted answer to SkillUseIntent. Damage and healing already reach clients as events and
// snapshots; mana and cooldowns come in ServerPlayerVitals.
public sealed record AbilityUsedResult(string Key, int TargetsHit, int DamageApplied, int HealthRestored) : IActionResultPayload;
