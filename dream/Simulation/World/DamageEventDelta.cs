using Dream.Simulation.Combat;

namespace Dream.Simulation.World;

public readonly record struct DamageEventDelta(
    int ActorId,
    CreatureKind Kind,
    int Amount,
    bool ActorDied,
    int CurrentHealth,
    int MaxHealth);
