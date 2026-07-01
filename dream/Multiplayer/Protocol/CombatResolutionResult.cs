using Dream.Simulation.Combat;

namespace Dream.Multiplayer.Protocol;

public sealed record CombatResolutionResult(
    int? TargetActorId,
    CreatureKind? TargetKind,
    int DamageApplied,
    bool TargetDied,
    int CurrentHealth,
    int MaxHealth) : IActionResultPayload;
