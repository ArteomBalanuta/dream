using OpenTK.Mathematics;

namespace Dream.Simulation.World.Geometry;

public static class CubeFracturePattern
{
    public static IReadOnlyList<GeometryFracturePiece> CreateFirstPassRubble(GeometryCellKey cell)
    {
        Random random = new(CreateSeed(cell));
        Vector3 cellCenter = new(cell.X + 0.5f, cell.Y + 0.5f, cell.Z + 0.5f);

        return
        [
            CreatePiece(cellCenter, random, quadrantX: -1f, quadrantZ: -1f),
            CreatePiece(cellCenter, random, quadrantX: 1f, quadrantZ: -1f),
            CreatePiece(cellCenter, random, quadrantX: -1f, quadrantZ: 1f),
            CreatePiece(cellCenter, random, quadrantX: 1f, quadrantZ: 1f)
        ];
    }

    private static GeometryFracturePiece CreatePiece(Vector3 cellCenter, Random random, float quadrantX, float quadrantZ)
    {
        float sideLength = Lerp(0.22f, 0.34f, random.NextSingle());
        float horizontalRadius = Lerp(0.08f, 0.18f, random.NextSingle());
        float verticalLift = Lerp(0.00f, 0.06f, random.NextSingle());
        float jitterX = Lerp(-0.03f, 0.03f, random.NextSingle());
        float jitterZ = Lerp(-0.03f, 0.03f, random.NextSingle());

        Vector3 center = new(
            cellCenter.X + (quadrantX * horizontalRadius) + jitterX,
            (cellCenter.Y - 0.5f) + (sideLength * 0.5f) + verticalLift,
            cellCenter.Z + (quadrantZ * horizontalRadius) + jitterZ);

        return new GeometryFracturePiece(center, sideLength);
    }

    private static int CreateSeed(GeometryCellKey cell)
    {
        unchecked
        {
            int hash = 17;
            hash = (hash * 31) + cell.X;
            hash = (hash * 31) + cell.Y;
            hash = (hash * 31) + cell.Z;
            return hash;
        }
    }

    private static float Lerp(float min, float max, float alpha)
    {
        return min + ((max - min) * alpha);
    }
}
