
namespace Dream.Simulation.Combat;

public readonly record struct DamageAppliedEvent(
    CombatActorHandle Actor,
    int Amount,
    FloatingCombatTextKind Kind);
