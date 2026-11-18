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

    // True while this seat flies through geometry: world collision, including the wolf separation push,
    // leaves it alone. Every seat holds its own Player with its own noclip toggle, so this is per seat and
    // not a property of the primary one. Defaulted so a target with no player behind it reads as off.
    bool IsNoclipEnabled => false;

    void SetWorldPosition(Vector3 position);
}
