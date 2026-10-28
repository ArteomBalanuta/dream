using OpenTK.Mathematics;
using Dream.Simulation.Determinism;

namespace Dream.Simulation.World.Geometry;

// Wire contract: protocol v3 sends unmoved rubble as k=0 ("the cell's first-pass rubble") and the
// client regenerates the pieces here (WorldSnapshotJsonConverter). Any change to this pattern (the
// salt, the anchors, the piece count, the random draws or their order) changes what k=0 means on
// the wire, so old and new peers would build different rubble; it needs a protocol version bump.
public static class CubeFracturePattern
{
    // "fracture" in ASCII; keeps rubble seeds apart from other per-cell seeds.
    private const ulong FractureSalt = 0x6672616374757265UL;

    private static readonly Vector2[] BaseAnchors =
    {
        new(-0.32f, -0.32f),
        new(0.00f, -0.34f),
        new(0.32f, -0.32f),
        new(-0.32f, 0.32f),
        new(0.00f, 0.34f),
        new(0.32f, 0.32f)
    };

    // Without an explicit generator the rubble is a pure function of the cell, so the server and
    // a client fallback that fracture the same cell produce the same pieces.
    public static IReadOnlyList<GeometryFracturePiece> CreateFirstPassRubble(
        GeometryCellKey cell,
        DeterministicRandom? random = null)
    {
        random ??= new DeterministicRandom(CreateSeed(cell));
        Vector3 cellCenter = new(cell.X + 0.5f, cell.Y + 0.5f, cell.Z + 0.5f);
        Vector2[] anchors = BaseAnchors.ToArray();
        Shuffle(anchors, random);

        int pieceCount = random.NextInt(5, anchors.Length + 1);
        GeometryFracturePiece[] pieces = new GeometryFracturePiece[pieceCount];
        for (int index = 0; index < pieces.Length; index++)
        {
            pieces[index] = CreatePiece(cellCenter, random, anchors[index]);
        }

        return pieces;
    }

    public static ulong CreateSeed(GeometryCellKey cell)
    {
        return Seed.Combine(FractureSalt, Seed.FromCell(cell.X, cell.Y, cell.Z));
    }

    private static GeometryFracturePiece CreatePiece(Vector3 cellCenter, DeterministicRandom random, Vector2 anchor)
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

    private static void Shuffle<T>(IList<T> items, DeterministicRandom random)
    {
        for (int index = items.Count - 1; index > 0; index--)
        {
            int swapIndex = random.NextInt(index + 1);
            (items[index], items[swapIndex]) = (items[swapIndex], items[index]);
        }
    }

    private static float Lerp(float min, float max, float alpha)
    {
        return min + ((max - min) * alpha);
    }
}
