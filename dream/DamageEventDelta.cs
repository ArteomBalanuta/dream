public readonly record struct DamageEventDelta(
    int ActorId,
    CreatureKind Kind,
    int Amount,
    bool ActorDied,
    int CurrentHealth,
    int MaxHealth);
