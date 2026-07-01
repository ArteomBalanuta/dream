namespace Dream.Simulation.World.Geometry;

public static class CubeFracturePattern
{
    public static IReadOnlySet<GeometrySubcellKey> CreateFirstPassRubble(int resolution)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resolution);

        int pieceSize = Math.Max(1, resolution / 3);
        int maxStart = Math.Max(0, resolution - pieceSize);

        HashSet<GeometrySubcellKey> pieces = [];
        AddPiece(pieces, 0, 0, 0, pieceSize);
        AddPiece(pieces, maxStart, 0, 0, pieceSize);
        AddPiece(pieces, 0, 0, maxStart, pieceSize);
        AddPiece(pieces, maxStart, 0, maxStart, pieceSize);
        return pieces;
    }

    private static void AddPiece(
        HashSet<GeometrySubcellKey> pieces,
        int startX,
        int startY,
        int startZ,
        int pieceSize)
    {
        for (int x = startX; x < startX + pieceSize; x++)
        {
            for (int y = startY; y < startY + pieceSize; y++)
            {
                for (int z = startZ; z < startZ + pieceSize; z++)
                {
                    pieces.Add(new GeometrySubcellKey(x, y, z));
                }
            }
        }
    }
}
