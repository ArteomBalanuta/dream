using OpenTK.Mathematics;

namespace Dream.Multiplayer.Protocol;


public sealed record BuildUseIntent(Vector3 PlacementTarget) : IPlayerActionPayload;
