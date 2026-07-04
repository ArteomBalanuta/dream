using OpenTK.Mathematics;
using Dream.Simulation.Combat;
using Dream.Simulation.Players;

namespace Dream.Simulation.World;


public readonly record struct ActorSnapshot(
    int ActorId,
    CreatureKind Kind,
    string? SessionId,
    Vector3 Position,
    Vector3 Facing,
    int CurrentHealth,
    int MaxHealth,
    bool IsAlive,
    PlayerInteractionMode? InteractionMode = null,
    PlayerCombatStatsSnapshot? CombatStats = null,
    string? DisplayName = null,
    PlayerProgressionSnapshot? Progression = null);
