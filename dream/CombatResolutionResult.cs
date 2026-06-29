public sealed record CombatResolutionResult(
    int? TargetActorId,
    CreatureKind? TargetKind,
    int DamageApplied,
    bool TargetDied,
    int CurrentHealth,
    int MaxHealth) : IActionResultPayload;
