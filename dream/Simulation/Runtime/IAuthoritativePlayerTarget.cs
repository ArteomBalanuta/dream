using OpenTK.Mathematics;
using Dream.Multiplayer.Sessions;
using Dream.Simulation.Players;

namespace Dream.Simulation.Runtime;


public interface IAuthoritativePlayerTarget
{
    PlayerSessionId SessionId { get; }

    int ActorId { get; }

    Vector3 Position { get; }

    Vector3 Facing { get; }

    Vector3 EyePosition { get; }

    PlayerCombatant Combatant { get; }

    PlayerInteractionMode InteractionMode { get; }

    bool IsAlive { get; }

    bool IsSpawned { get; }

    void SetWorldPosition(Vector3 position);
}
