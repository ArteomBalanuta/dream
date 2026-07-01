using OpenTK.Mathematics;

namespace Dream.Simulation.Combat;

public readonly record struct CombatTargetCandidate(
    CombatActorHandle Actor,
    Vector3 Anchor,
    int Priority);
