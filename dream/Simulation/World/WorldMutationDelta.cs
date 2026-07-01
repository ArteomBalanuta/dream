using OpenTK.Mathematics;

namespace Dream.Simulation.World;


public enum WorldMutationKind
{
    CubePlaced,
    CubeDestroyed
}

public readonly record struct WorldMutationDelta(
    WorldMutationKind Kind,
    Vector3 CellPosition);
