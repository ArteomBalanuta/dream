using OpenTK.Mathematics;

namespace Dream.Multiplayer.Protocol;


public sealed record CombatUseIntent(
    Vector3 RayOrigin,
    Vector3 RayDirection,
    int? TargetActorId = null) : IPlayerActionPayload;
