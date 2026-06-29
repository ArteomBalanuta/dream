using OpenTK.Mathematics;

public sealed record BuildUseIntent(Vector3 PlacementTarget) : IPlayerActionPayload;
