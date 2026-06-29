using OpenTK.Mathematics;

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
