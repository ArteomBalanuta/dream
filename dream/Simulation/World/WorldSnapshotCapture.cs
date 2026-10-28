using System.Buffers;
using System.Collections;
using OpenTK.Mathematics;
using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.World;

// The replicated world in flat arrays rented from ArrayPool (SCALE-1). The server fills one on the
// tick (WorldSessionRuntime.CaptureWorldSnapshot): one pass over the map's cells, no LINQ and no
// per-cube objects. The protocol's compact world writer encodes it later, on the connection
// writer, straight from these arrays.
//
// ToWorldSnapshot wraps it in a WorldSnapshot whose lists are read-only views over the arrays, so
// every existing reader of WorldSnapshot keeps working. A capture that reaches a ServerConnection
// world transfer is released when its last chunk is encoded: the arrays go back to the pool and
// every view then throws ObjectDisposedException. A capture nobody releases is simply collected.
//
// Layout: cube i has its cell at CubeCells[3i..3i+2], its exact center at CubeCenters[3i..3i+2],
// its material at CubeMaterials[i], and a replicated id at CubeIds[i] when CubeHasId[i]. Subcell
// cell i is at SubcellCells[3i..3i+2]. Rubble cell i has its origin cell at RubbleCells[3i..3i+2],
// its material at RubbleMaterials[i], and pieces RubblePieceStarts[i] to RubblePieceStarts[i+1]-1,
// piece p being center Pieces[4p..4p+2] and side length Pieces[4p+3].
public sealed class WorldSnapshotCapture
{
    private const int MinimumCapacity = 16;

    internal int[] CubeCells = [];
    internal float[] CubeCenters = [];
    internal int[] CubeMaterials = [];
    internal int[] CubeIds = [];
    internal bool[] CubeHasId = [];
    internal int[] SubcellCells = [];
    internal int[] RubbleCells = [];
    internal int[] RubbleMaterials = [];
    internal int[] RubblePieceStarts = [];
    internal float[] Pieces = [];
    private int _released;
    private bool _rubbleOpen;

    private WorldSnapshotCapture(int width, int height, int depth, ulong? mutationSequence)
    {
        Width = width;
        Height = height;
        Depth = depth;
        MutationSequence = mutationSequence;
    }

    public int Width { get; }

    public int Height { get; }

    public int Depth { get; }

    public ulong? MutationSequence { get; }

    public int CubeCount { get; private set; }

    public int SubcellCount { get; private set; }

    public int RubbleCount { get; private set; }

    public int PieceCount { get; private set; }

    public bool IsReleased => Volatile.Read(ref _released) != 0;

    // Capacities are hints (the arrays grow), so an exact count is not needed up front.
    public static WorldSnapshotCapture Rent(
        int width,
        int height,
        int depth,
        ulong? mutationSequence,
        int cubeCapacity,
        int subcellCapacity,
        int rubbleCapacity,
        int pieceCapacity)
    {
        WorldSnapshotCapture capture = new(width, height, depth, mutationSequence);
        cubeCapacity = Math.Max(MinimumCapacity, cubeCapacity);
        capture.CubeCells = ArrayPool<int>.Shared.Rent(cubeCapacity * 3);
        capture.CubeCenters = ArrayPool<float>.Shared.Rent(cubeCapacity * 3);
        capture.CubeMaterials = ArrayPool<int>.Shared.Rent(cubeCapacity);
        capture.CubeIds = ArrayPool<int>.Shared.Rent(cubeCapacity);
        capture.CubeHasId = ArrayPool<bool>.Shared.Rent(cubeCapacity);
        capture.SubcellCells = ArrayPool<int>.Shared.Rent(Math.Max(MinimumCapacity, subcellCapacity) * 3);
        rubbleCapacity = Math.Max(MinimumCapacity, rubbleCapacity);
        capture.RubbleCells = ArrayPool<int>.Shared.Rent(rubbleCapacity * 3);
        capture.RubbleMaterials = ArrayPool<int>.Shared.Rent(rubbleCapacity);
        capture.RubblePieceStarts = ArrayPool<int>.Shared.Rent(rubbleCapacity + 1);
        capture.RubblePieceStarts[0] = 0;
        capture.Pieces = ArrayPool<float>.Shared.Rent(Math.Max(MinimumCapacity, pieceCapacity) * 4);
        return capture;
    }

    // A capture of a WorldSnapshot built from plain lists (decoded, or made by hand in tests), so the
    // compact writer has one source shape. Not pooled-sensitive: callers may release it or not.
    public static WorldSnapshotCapture FromSnapshot(in WorldSnapshot snapshot)
    {
        if (TryGetCapture(snapshot, out WorldSnapshotCapture? existing))
        {
            return existing;
        }

        IReadOnlyList<PlacedCubeSnapshot> placed = ResolvePlacedCubes(snapshot);
        IReadOnlyList<Vector3> subcells = snapshot.AuthoredSubcellCells ?? Array.Empty<Vector3>();
        IReadOnlyList<FracturedCubeSnapshot> fractured = ResolveFracturedCubes(snapshot);
        int pieceCount = 0;
        for (int i = 0; i < fractured.Count; i++)
        {
            pieceCount += fractured[i].Pieces?.Count ?? 0;
        }

        WorldSnapshotCapture capture = Rent(
            snapshot.Width,
            snapshot.Height,
            snapshot.Depth,
            snapshot.MutationSequence,
            placed.Count,
            subcells.Count,
            fractured.Count,
            pieceCount);
        for (int i = 0; i < placed.Count; i++)
        {
            PlacedCubeSnapshot cube = placed[i];
            (int x, int y, int z) = ToCellKey(cube.CellPosition);
            capture.AddCube(x, y, z, cube.MaterialId, cube.ActorId, cube.CenterPosition);
        }

        for (int i = 0; i < subcells.Count; i++)
        {
            (int x, int y, int z) = ToCellKey(subcells[i]);
            capture.AddSubcell(x, y, z);
        }

        for (int i = 0; i < fractured.Count; i++)
        {
            FracturedCubeSnapshot rubble = fractured[i];
            (int x, int y, int z) = ToCellKey(rubble.CellPosition);
            capture.BeginRubble(x, y, z, rubble.MaterialId);
            IReadOnlyList<GeometryFracturePiece> pieces = rubble.Pieces ?? Array.Empty<GeometryFracturePiece>();
            for (int p = 0; p < pieces.Count; p++)
            {
                capture.AddPiece(pieces[p]);
            }

            capture.EndRubble();
        }

        return capture;
    }

    // The capture behind a WorldSnapshot made by ToWorldSnapshot, if any.
    public static bool TryGetCapture(in WorldSnapshot snapshot, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out WorldSnapshotCapture? capture)
    {
        if (snapshot.PlacedCubes is CapturedPlacedCubeList view)
        {
            capture = view.Capture;
            return true;
        }

        capture = null;
        return false;
    }

    public void AddCube(int x, int y, int z, GeometryMaterialId materialId, int? actorId, Vector3 center)
    {
        ThrowIfReleased();
        int index = CubeCount;
        EnsureCapacity(ref CubeCells, (index + 1) * 3);
        EnsureCapacity(ref CubeCenters, (index + 1) * 3);
        EnsureCapacity(ref CubeMaterials, index + 1);
        EnsureCapacity(ref CubeIds, index + 1);
        EnsureCapacity(ref CubeHasId, index + 1);
        int at = index * 3;
        CubeCells[at] = x;
        CubeCells[at + 1] = y;
        CubeCells[at + 2] = z;
        CubeCenters[at] = center.X;
        CubeCenters[at + 1] = center.Y;
        CubeCenters[at + 2] = center.Z;
        CubeMaterials[index] = (int)materialId;
        CubeIds[index] = actorId ?? 0;
        CubeHasId[index] = actorId.HasValue;
        CubeCount = index + 1;
    }

    public void AddSubcell(int x, int y, int z)
    {
        ThrowIfReleased();
        int at = SubcellCount * 3;
        EnsureCapacity(ref SubcellCells, at + 3);
        SubcellCells[at] = x;
        SubcellCells[at + 1] = y;
        SubcellCells[at + 2] = z;
        SubcellCount++;
    }

    public void BeginRubble(int x, int y, int z, GeometryMaterialId materialId)
    {
        ThrowIfReleased();
        if (_rubbleOpen)
        {
            throw new InvalidOperationException("EndRubble must close the previous rubble cell first.");
        }

        int index = RubbleCount;
        EnsureCapacity(ref RubbleCells, (index + 1) * 3);
        EnsureCapacity(ref RubbleMaterials, index + 1);
        EnsureCapacity(ref RubblePieceStarts, index + 2);
        int at = index * 3;
        RubbleCells[at] = x;
        RubbleCells[at + 1] = y;
        RubbleCells[at + 2] = z;
        RubbleMaterials[index] = (int)materialId;
        _rubbleOpen = true;
    }

    public void AddPiece(GeometryFracturePiece piece)
    {
        ThrowIfReleased();
        if (!_rubbleOpen)
        {
            throw new InvalidOperationException("AddPiece needs a rubble cell opened by BeginRubble.");
        }

        int at = PieceCount * 4;
        EnsureCapacity(ref Pieces, at + 4);
        Pieces[at] = piece.Center.X;
        Pieces[at + 1] = piece.Center.Y;
        Pieces[at + 2] = piece.Center.Z;
        Pieces[at + 3] = piece.SideLength;
        PieceCount++;
    }

    public void EndRubble()
    {
        if (!_rubbleOpen)
        {
            throw new InvalidOperationException("EndRubble needs a rubble cell opened by BeginRubble.");
        }

        RubbleCount++;
        RubblePieceStarts[RubbleCount] = PieceCount;
        _rubbleOpen = false;
    }

    public Vector3 GetCubeCell(int index)
    {
        int at = index * 3;
        return new Vector3(CubeCells[at], CubeCells[at + 1], CubeCells[at + 2]);
    }

    public Vector3 GetCubeCenter(int index)
    {
        int at = index * 3;
        return new Vector3(CubeCenters[at], CubeCenters[at + 1], CubeCenters[at + 2]);
    }

    public Vector3 GetSubcellCell(int index)
    {
        int at = index * 3;
        return new Vector3(SubcellCells[at], SubcellCells[at + 1], SubcellCells[at + 2]);
    }

    public Vector3 GetRubbleCell(int index)
    {
        int at = index * 3;
        return new Vector3(RubbleCells[at], RubbleCells[at + 1], RubbleCells[at + 2]);
    }

    public GeometryFracturePiece GetPiece(int pieceIndex)
    {
        int at = pieceIndex * 4;
        return new GeometryFracturePiece(new Vector3(Pieces[at], Pieces[at + 1], Pieces[at + 2]), Pieces[at + 3]);
    }

    // A WorldSnapshot over this capture. Its lists are views (no copy); PlacedCubeCells holds the
    // cubes' cells and FracturedCubeCells the rubble cells, as the list-based export did.
    public WorldSnapshot ToWorldSnapshot()
    {
        ThrowIfReleased();
        return new WorldSnapshot(
            Width,
            Height,
            Depth,
            new CapturedCellList(this, CapturedCellList.Source.Cubes),
            new CapturedCellList(this, CapturedCellList.Source.Rubble),
            new CapturedCellList(this, CapturedCellList.Source.Subcells),
            new CapturedPlacedCubeList(this),
            new CapturedFracturedCubeList(this),
            MutationSequence);
    }

    // Returns the arrays to the pool. Idempotent; the views throw afterwards.
    public void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        ArrayPool<int>.Shared.Return(CubeCells);
        ArrayPool<float>.Shared.Return(CubeCenters);
        ArrayPool<int>.Shared.Return(CubeMaterials);
        ArrayPool<int>.Shared.Return(CubeIds);
        ArrayPool<bool>.Shared.Return(CubeHasId);
        ArrayPool<int>.Shared.Return(SubcellCells);
        ArrayPool<int>.Shared.Return(RubbleCells);
        ArrayPool<int>.Shared.Return(RubbleMaterials);
        ArrayPool<int>.Shared.Return(RubblePieceStarts);
        ArrayPool<float>.Shared.Return(Pieces);
        CubeCells = [];
        CubeCenters = [];
        CubeMaterials = [];
        CubeIds = [];
        CubeHasId = [];
        SubcellCells = [];
        RubbleCells = [];
        RubbleMaterials = [];
        RubblePieceStarts = [];
        Pieces = [];
    }

    internal void ThrowIfReleased()
    {
        ObjectDisposedException.ThrowIf(IsReleased, this);
    }

    private static void EnsureCapacity<T>(ref T[] array, int required)
    {
        if (array.Length >= required)
        {
            return;
        }

        T[] grown = ArrayPool<T>.Shared.Rent(Math.Max(required, array.Length * 2));
        Array.Copy(array, grown, array.Length);
        if (array.Length > 0)
        {
            ArrayPool<T>.Shared.Return(array);
        }

        array = grown;
    }

    private static IReadOnlyList<PlacedCubeSnapshot> ResolvePlacedCubes(in WorldSnapshot snapshot)
    {
        if (snapshot.PlacedCubes is { Count: > 0 } placed)
        {
            return placed;
        }

        // A snapshot with cells only: cubes at their cell centers, in the default material.
        IReadOnlyList<Vector3> cells = snapshot.PlacedCubeCells ?? Array.Empty<Vector3>();
        PlacedCubeSnapshot[] cubes = new PlacedCubeSnapshot[cells.Count];
        for (int i = 0; i < cubes.Length; i++)
        {
            (int x, int y, int z) = ToCellKey(cells[i]);
            cubes[i] = new PlacedCubeSnapshot(cells[i], new Vector3(x + 0.5f, y + 0.5f, z + 0.5f));
        }

        return cubes;
    }

    private static IReadOnlyList<FracturedCubeSnapshot> ResolveFracturedCubes(in WorldSnapshot snapshot)
    {
        if (snapshot.FracturedCubes is { Count: > 0 } fractured)
        {
            return fractured;
        }

        // Cells only: the client regenerates their first-pass rubble (an empty piece list).
        IReadOnlyList<Vector3> cells = snapshot.FracturedCubeCells ?? Array.Empty<Vector3>();
        FracturedCubeSnapshot[] rubble = new FracturedCubeSnapshot[cells.Count];
        for (int i = 0; i < rubble.Length; i++)
        {
            rubble[i] = new FracturedCubeSnapshot(cells[i], GeometryMaterialId.Grass, Array.Empty<GeometryFracturePiece>());
        }

        return rubble;
    }

    internal static (int X, int Y, int Z) ToCellKey(Vector3 cellPosition)
    {
        return (
            (int)MathF.Floor(cellPosition.X + 1e-6f),
            (int)MathF.Floor(cellPosition.Y + 1e-6f),
            (int)MathF.Floor(cellPosition.Z + 1e-6f));
    }

    internal abstract class CapturedList<T> : IReadOnlyList<T>
    {
        protected CapturedList(WorldSnapshotCapture capture)
        {
            Capture = capture;
        }

        public WorldSnapshotCapture Capture { get; }

        public abstract int Count { get; }

        public T this[int index]
        {
            get
            {
                Capture.ThrowIfReleased();
                if ((uint)index >= (uint)Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }

                return Get(index);
            }
        }

        protected abstract T Get(int index);

        public IEnumerator<T> GetEnumerator()
        {
            int count = Count;
            for (int i = 0; i < count; i++)
            {
                yield return this[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CapturedCellList : CapturedList<Vector3>
    {
        public enum Source
        {
            Cubes,
            Subcells,
            Rubble
        }

        private readonly Source _source;

        public CapturedCellList(WorldSnapshotCapture capture, Source source)
            : base(capture)
        {
            _source = source;
        }

        public override int Count => _source switch
        {
            Source.Cubes => Capture.CubeCount,
            Source.Subcells => Capture.SubcellCount,
            _ => Capture.RubbleCount
        };

        protected override Vector3 Get(int index) => _source switch
        {
            Source.Cubes => Capture.GetCubeCell(index),
            Source.Subcells => Capture.GetSubcellCell(index),
            _ => Capture.GetRubbleCell(index)
        };
    }

    private sealed class CapturedFracturedCubeList : CapturedList<FracturedCubeSnapshot>
    {
        public CapturedFracturedCubeList(WorldSnapshotCapture capture)
            : base(capture)
        {
        }

        public override int Count => Capture.RubbleCount;

        protected override FracturedCubeSnapshot Get(int index)
        {
            int first = Capture.RubblePieceStarts[index];
            int end = Capture.RubblePieceStarts[index + 1];
            GeometryFracturePiece[] pieces = end == first ? Array.Empty<GeometryFracturePiece>() : new GeometryFracturePiece[end - first];
            for (int p = 0; p < pieces.Length; p++)
            {
                pieces[p] = Capture.GetPiece(first + p);
            }

            return new FracturedCubeSnapshot(
                Capture.GetRubbleCell(index),
                (GeometryMaterialId)Capture.RubbleMaterials[index],
                pieces);
        }
    }

    internal sealed class CapturedPlacedCubeList : CapturedList<PlacedCubeSnapshot>
    {
        public CapturedPlacedCubeList(WorldSnapshotCapture capture)
            : base(capture)
        {
        }

        public override int Count => Capture.CubeCount;

        protected override PlacedCubeSnapshot Get(int index)
        {
            return new PlacedCubeSnapshot(
                Capture.GetCubeCell(index),
                Capture.GetCubeCenter(index),
                (GeometryMaterialId)Capture.CubeMaterials[index],
                Capture.CubeHasId[index] ? Capture.CubeIds[index] : null);
        }
    }
}
