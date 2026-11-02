using Dream.Simulation.Inventory;

namespace Dream.Client.Presentation;

// What a remote player shows besides its pose. The held visuals come from the replicated VisualKey
// ("<mainHandVisualId>|<offHandVisualId>"); the attack and hit serials count events the client
// inferred from snapshots (a swing at an enemy in front, health lost); Airborne and HorizontalSpeed
// come from the movement between snapshots; DeathAgeSeconds is how long the player has been dead
// (infinity when it has not been seen alive), so the death animation can play before the avatar hides.
public readonly record struct RemotePlayerPresentationState(
    int ActorId,
    string SessionId,
    string DisplayName,
    InterpolatedActorPose Pose,
    int CurrentHealth,
    int MaxHealth,
    bool IsAlive,
    bool IsMoving = false,
    HeldLightKind HeldLight = HeldLightKind.None,
    int Level = 1,
    string? MainHandVisual = null,
    string? OffHandVisual = null,
    int AttackSerial = 0,
    int HitSerial = 0,
    bool Airborne = false,
    float HorizontalSpeed = 0f,
    float DeathAgeSeconds = float.PositiveInfinity);
