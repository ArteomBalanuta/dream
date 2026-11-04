using OpenTK.Mathematics;
using Dream.Rendering.Actors;
using Dream.Simulation.Inventory;

namespace Dream.Client.Presentation;

// What a remote player shows besides its pose. The held and worn visuals come from the replicated VisualKey
// ("main|off|head|body|hands|feet|legs|back|companion", trailing empties trimmed); the attack and hit serials count events the client
// inferred from snapshots (a swing at an enemy in front, health lost); Airborne and HorizontalSpeed
// come from the movement between snapshots, as does TravelDirection (a smoothed unit ground-plane
// direction for the leg twist, zero when not moving); DeathAgeSeconds is how long the player has been dead
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
    PlayerEquipmentVisuals Equipment = default,
    int AttackSerial = 0,
    int HitSerial = 0,
    bool Airborne = false,
    float HorizontalSpeed = 0f,
    float DeathAgeSeconds = float.PositiveInfinity,
    Vector3 TravelDirection = default);
