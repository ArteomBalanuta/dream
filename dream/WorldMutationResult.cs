using OpenTK.Mathematics;

public sealed record WorldMutationResult(
    WorldMutationKind Kind,
    Vector3 CellPosition,
    bool Applied) : IActionResultPayload;
