using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;

namespace Dream.Rendering.Props;

// The ground of the replicated static content, one entry per (x, z) column: the top face of the highest solid
// cell, that cell's material, and whether anything stands on it that a prop must not overlap (a lamp on top, or
// a subdivided cell above the surface, which is how the sculptures are stored). Built once per static content
// version by PropIndex, from the same export PlacedLightIndex scans, so every client derives the same ground.
public sealed class StaticGround
{
    public readonly record struct Column(int TopY, GeometryMaterialId Material, bool Blocked);

    private struct Accumulator
    {
        public int TopSolidY;
        public GeometryMaterialId TopMaterial;
        public int TopNonSolidY;
        public bool HasSolid;
    }

    private readonly Dictionary<long, Accumulator> _building = [];
    private readonly Dictionary<long, Column> _columns = [];

    public int MaxX { get; private set; }

    public int MaxZ { get; private set; }

    public int ColumnCount => _columns.Count;

    private static long Pack(int x, int z) => ((long)x << 32) | (uint)z;

    public void Clear()
    {
        _building.Clear();
        _columns.Clear();
        MaxX = 0;
        MaxZ = 0;
    }

    public void Add(int x, int y, int z, bool solid, GeometryMaterialId material)
    {
        long key = Pack(x, z);
        _building.TryGetValue(key, out Accumulator a);
        if (solid)
        {
            if (!a.HasSolid || y > a.TopSolidY)
            {
                a.TopSolidY = y;
                a.TopMaterial = material;
                a.HasSolid = true;
            }
        }
        else if (y > a.TopNonSolidY)
        {
            a.TopNonSolidY = y;
        }

        _building[key] = a;
    }

    // Call after the last Add.
    public void Seal()
    {
        _columns.Clear();
        foreach ((long key, Accumulator a) in _building)
        {
            if (!a.HasSolid)
            {
                continue;
            }

            int x = (int)(key >> 32);
            int z = (int)(uint)key;
            bool blocked = a.TopMaterial == GeometryMaterialId.Lamp || a.TopNonSolidY > a.TopSolidY;
            _columns[key] = new Column(a.TopSolidY, a.TopMaterial, blocked);
            MaxX = Math.Max(MaxX, x);
            MaxZ = Math.Max(MaxZ, z);
        }

        _building.Clear();
    }

    public bool TryGet(int x, int z, out Column column) => _columns.TryGetValue(Pack(x, z), out column);

    // Scans the cells the way PlacedLightIndex does: the export's static cells, else the static coarse cubes.
    public void Rebuild(in GeometryRenderExport export)
    {
        Clear();
        IReadOnlyList<GeometryRenderCell>? staticCells = export.StaticCells;
        if (staticCells != null)
        {
            for (int i = 0; i < staticCells.Count; i++)
            {
                GeometryRenderCell cell = staticCells[i];
                Add(cell.Key.X, cell.Key.Y, cell.Key.Z, cell.Cell.Definition.IsSolid, cell.Cell.Definition.MaterialId);
            }
        }
        else if (export.CompatibilityCubes != null)
        {
            IReadOnlyList<Cube> cubes = export.CompatibilityCubes;
            for (int i = 0; i < cubes.Count; i++)
            {
                Cube cube = cubes[i];
                if (!cube.IsEmpty)
                {
                    Add(
                        (int)MathF.Floor(cube.Position.X),
                        (int)MathF.Floor(cube.Position.Y),
                        (int)MathF.Floor(cube.Position.Z),
                        cube.IsSolid,
                        cube.MaterialId);
                }
            }
        }

        Seal();
    }
}
