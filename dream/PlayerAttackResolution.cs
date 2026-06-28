public readonly record struct PlayerAttackResolution(
    int Damage,
    bool IsCritical,
    FloatingCombatTextKind FloatingTextKind);
