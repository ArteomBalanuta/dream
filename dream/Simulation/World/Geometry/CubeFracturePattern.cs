using OpenTK.Mathematics;

namespace Dream.Simulation.World.Geometry;

public static class CubeFracturePattern
{
    private static readonly Vector2[] BaseAnchors =
    {
        new(-0.32f, -0.32f),
        new(0.00f, -0.34f),
        new(0.32f, -0.32f),
        new(-0.32f, 0.32f),
        new(0.00f, 0.34f),
        new(0.32f, 0.32f)
    };

    public static IReadOnlyList<GeometryFracturePiece> CreateFirstPassRubble(
        GeometryCellKey cell,
        Random? random = null)
    {
        random ??= Random.Shared;
        Vector3 cellCenter = new(cell.X + 0.5f, cell.Y + 0.5f, cell.Z + 0.5f);
        Vector2[] anchors = BaseAnchors.ToArray();
        Shuffle(anchors, random);

        int pieceCount = random.Next(5, anchors.Length + 1);
        GeometryFracturePiece[] pieces = new GeometryFracturePiece[pieceCount];
        for (int index = 0; index < pieces.Length; index++)
        {
            pieces[index] = CreatePiece(cellCenter, random, anchors[index]);
        }

        return pieces;
    }

    private static GeometryFracturePiece CreatePiece(Vector3 cellCenter, Random random, Vector2 anchor)
    {
        float sideLength = Lerp(0.16f, 0.23f, random.NextSingle());
        float verticalLift = Lerp(0.00f, 0.18f, random.NextSingle());
        float jitterX = Lerp(-0.025f, 0.025f, random.NextSingle());
        float jitterZ = Lerp(-0.025f, 0.025f, random.NextSingle());

        Vector3 center = new(
            cellCenter.X + anchor.X + jitterX,
            (cellCenter.Y - 0.5f) + (sideLength * 0.5f) + verticalLift,
            cellCenter.Z + anchor.Y + jitterZ);

        return new GeometryFracturePiece(center, sideLength);
    }

    private static void Shuffle<T>(IList<T> items, Random random)
    {
        for (int index = items.Count - 1; index > 0; index--)
        {
            int swapIndex = random.Next(index + 1);
            (items[index], items[swapIndex]) = (items[swapIndex], items[index]);
        }
    }

    private static float Lerp(float min, float max, float alpha)
    {
        return min + ((max - min) * alpha);
    }
}
