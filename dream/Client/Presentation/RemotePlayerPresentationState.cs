using Dream.Simulation.Inventory;

namespace Dream.Client.Presentation;

public readonly record struct RemotePlayerPresentationState(
    int ActorId,
    string SessionId,
    string DisplayName,
    InterpolatedActorPose Pose,
    int CurrentHealth,
    int MaxHealth,
    bool IsAlive,
    bool IsMoving = false,
    HeldLightKind HeldLight = HeldLightKind.None);
