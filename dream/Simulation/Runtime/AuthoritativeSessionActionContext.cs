using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;

namespace Dream.Simulation.Runtime;

public sealed record AuthoritativeSessionActionContext(
    ConnectedPlayer Player,
    PlayerCombatant Combatant,
    PlayerInteractionMode InteractionMode,
    OpenTK.Mathematics.Vector3 CombatRayOrigin,
    OpenTK.Mathematics.Vector3 CombatRayDirection,
    bool SupportsBuildActions,
    bool SupportsCombatActions);
