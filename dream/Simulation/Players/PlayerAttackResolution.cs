using Dream.Simulation.Combat;

namespace Dream.Simulation.Players;

public readonly record struct PlayerAttackResolution(
    int Damage,
    bool IsCritical,
    FloatingCombatTextKind FloatingTextKind);
