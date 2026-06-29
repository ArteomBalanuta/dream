using OpenTK.Mathematics;

public enum WorldMutationKind
{
    CubePlaced,
    CubeDestroyed
}

public readonly record struct WorldMutationDelta(
    WorldMutationKind Kind,
    Vector3 CellPosition);
