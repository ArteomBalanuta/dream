using Dream.Rendering.Props;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;

namespace Dream.Rendering.Ground;

// The ground scatter of the current static world, rebuilt only when the prop layout it follows was rebuilt (the
// same change signal the props use: PropIndex.RebuildCount) or the asset set changed. The placements are grouped for
// culling: sorted by asset, then by 8x8-cell block. Each non-empty spatial block is one Cell with a box that holds
// everything of every asset inside it (radius and height included), so the renderer tests visibility once per cell per
// frame; each (asset, cell) run is one Block that names its cell. Steady state allocates nothing.
public sealed class GroundScatterIndex
{
    public const int BlockCells = 8;

    // A run of placements of one asset inside one spatial block (Cell indexes Cells).
    public readonly record struct Block(int Asset, int Start, int Count, int Cell);

    // One 8x8-cell spatial block that holds any placement, with the box that holds them all.
    public readonly record struct Cell(Vector3 Min, Vector3 Max);

    private readonly List<GroundPlacement> _placements = [];
    private readonly List<Block> _blocks = [];
    private readonly List<Cell> _cells = [];
    private GroundAssetSet _assets = GroundAssetSet.Empty;
    private int _propRebuilds = -1;
    private bool _hasScan;

    // Times the scatter was rebuilt (tests).
    public int RebuildCount { get; private set; }

    public GroundAssetSet Assets => _assets;

    // Sorted by asset, then block, shared and reused: callers must not mutate.
    public IReadOnlyList<GroundPlacement> Placements => _placements;

    public IReadOnlyList<Block> Blocks => _blocks;

    public IReadOnlyList<Cell> Cells => _cells;

    // Once per frame after PropIndex.Update.
    public void Update(PropIndex props, GroundAssetSet assets)
    {
        ArgumentNullException.ThrowIfNull(props);
        ArgumentNullException.ThrowIfNull(assets);
        if (_hasScan && props.RebuildCount == _propRebuilds && ReferenceEquals(assets, _assets))
        {
            return;
        }

        Rebuild(props.Ground, props.Placements, assets);
        _propRebuilds = props.RebuildCount;
        _hasScan = true;
    }

    // Tests: the same build without a PropIndex.
    public void Rebuild(StaticGround ground, IReadOnlyList<PropPlacement> props, GroundAssetSet assets)
    {
        _assets = assets;
        GroundScatterLayout.Build(ground, props, assets, _placements);
        IReadOnlyList<GroundAsset> list = assets.Assets;
        _placements.Sort(static (a, b) =>
        {
            int c = a.Asset.CompareTo(b.Asset);
            if (c != 0)
            {
                return c;
            }

            c = BlockOf(a.Position.Z).CompareTo(BlockOf(b.Position.Z));
            if (c != 0)
            {
                return c;
            }

            c = BlockOf(a.Position.X).CompareTo(BlockOf(b.Position.X));
            if (c != 0)
            {
                return c;
            }

            c = a.Position.X.CompareTo(b.Position.X);
            return c != 0 ? c : a.Position.Z.CompareTo(b.Position.Z);
        });

        _blocks.Clear();
        _cells.Clear();
        Dictionary<(int X, int Z), int> cellOf = [];
        for (int i = 0; i < _placements.Count; i++)
        {
            GroundPlacement p = _placements[i];
            GroundAsset asset = list[p.Asset];
            float r = asset.Radius * p.Scale;
            Vector3 lo = new(p.Position.X - r, p.Position.Y - 0.3f, p.Position.Z - r);
            Vector3 hi = new(p.Position.X + r, p.Position.Y + (asset.Height * p.Scale) + 0.3f, p.Position.Z + r);
            (int X, int Z) key = (BlockOf(p.Position.X), BlockOf(p.Position.Z));
            if (cellOf.TryGetValue(key, out int existing))
            {
                Cell c = _cells[existing];
                _cells[existing] = new Cell(Vector3.ComponentMin(c.Min, lo), Vector3.ComponentMax(c.Max, hi));
            }
            else
            {
                cellOf[key] = _cells.Count;
                _cells.Add(new Cell(lo, hi));
            }
        }

        int start = 0;
        while (start < _placements.Count)
        {
            GroundPlacement first = _placements[start];
            int bx = BlockOf(first.Position.X);
            int bz = BlockOf(first.Position.Z);
            int end = start + 1;
            while (end < _placements.Count && _placements[end].Asset == first.Asset &&
                BlockOf(_placements[end].Position.X) == bx && BlockOf(_placements[end].Position.Z) == bz)
            {
                end++;
            }

            _blocks.Add(new Block(first.Asset, start, end - start, cellOf[(bx, bz)]));
            start = end;
        }

        RebuildCount++;
    }

    private static int BlockOf(float coordinate) => (int)MathF.Floor(coordinate) / BlockCells;
}
