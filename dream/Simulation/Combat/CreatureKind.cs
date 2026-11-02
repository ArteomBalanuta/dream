using Dream.Simulation.Players;

namespace Dream.Simulation.Combat;

public enum CreatureKind
{
    Player,
    Enemy,
    Object,
    // A content NPC (M3): replicated in the actor snapshots but not an enemy and not in physics.
    Npc
}
