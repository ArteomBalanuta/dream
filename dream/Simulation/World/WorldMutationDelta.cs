using OpenTK.Mathematics;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.World;


public enum WorldMutationKind
{
    CubePlaced,
    CubeDestroyed,
    CubeFractured,
    CubeMoved,
    // A cube the client already has (generated terrain) got a replicated id on the server, for
    // example on its first hit. Carries the cube's cell and the id (ActorId); changes no geometry.
    CubeIdentified
}

public readonly record struct WorldMutationDelta(
    WorldMutationKind Kind,
    Vector3 CellPosition,
    Vector3? TargetCellPosition = null,
    Vector3? CenterPosition = null,
    GeometryMaterialId? MaterialId = null,
    IReadOnlyList<GeometryFracturePiece>? FracturePieces = null,
    ulong Sequence = 0,
    int? ActorId = null)
{
    public static WorldMutationDelta Moved(Vector3 sourceCellPosition, Vector3 authoritativeCenter)
    {
        return new WorldMutationDelta(
            WorldMutationKind.CubeMoved,
            sourceCellPosition,
            TargetCellPosition: ToCellPosition(authoritativeCenter),
            CenterPosition: authoritativeCenter);
    }

    public static WorldMutationDelta Fractured(
        Vector3 cellPosition,
        GeometryMaterialId materialId,
        IReadOnlyList<GeometryFracturePiece> fracturePieces)
    {
        return new WorldMutationDelta(
            WorldMutationKind.CubeFractured,
            cellPosition,
            MaterialId: materialId,
            FracturePieces: fracturePieces.ToArray());
    }

    public static WorldMutationDelta Identified(Vector3 cellPosition, int actorId)
    {
        return new WorldMutationDelta(
            WorldMutationKind.CubeIdentified,
            cellPosition,
            ActorId: actorId);
    }

    private static Vector3 ToCellPosition(Vector3 position)
    {
        return new Vector3(
            MathF.Floor(position.X + 1e-6f),
            MathF.Floor(position.Y + 1e-6f),
            MathF.Floor(position.Z + 1e-6f));
    }
}
