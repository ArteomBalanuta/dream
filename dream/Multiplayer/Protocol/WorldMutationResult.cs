using OpenTK.Mathematics;
using Dream.Simulation.World;

namespace Dream.Multiplayer.Protocol;


public sealed record WorldMutationResult(
    WorldMutationKind Kind,
    Vector3 CellPosition,
    bool Applied) : IActionResultPayload;
