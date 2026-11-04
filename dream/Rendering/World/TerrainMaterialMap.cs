using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;

namespace Dream.Rendering.World;

// A dense CPU map of the static world's full unit cells, one byte per cell, from which the terrain
// shader blends a top face's material with its neighbours'. Index = i + SizeX * (j + SizeY * k) for
// the cell (Origin.X + i, Origin.Y + j, Origin.Z + k); x and z are horizontal, y is up.
//
// Texel values: 0 no full unit cell (air, or a partial / sub-cell block); 1..8 the material id + 1 of
// a full cell whose cell above is not full (an exposed top); CoveredTexel a full blendable cell
// whose top is covered; NonBlendableTexel a full cell of a material outside the blend set (Lamp).
public sealed class TerrainMaterialMap
{
    public const byte EmptyTexel = 0;
    public const byte CoveredTexel = 254;
    public const byte NonBlendableTexel = 255;
    // Materials 0..BlendableMaterialCount-1 (Grass..GrassPath) blend.
    public const int BlendableMaterialCount = 8;
    // Per-dimension cap; a world larger than this gets no map (size 0).
    public const int MaxDimension = 256;
    private const float FullCellTolerance = 1e-3f;

    private byte[] _texels = [];

    // The buffer; only the first Count bytes mean anything. It is reused and only ever grows.
    public byte[] Texels => _texels;

    public Vector3i Origin { get; private set; }

    public Vector3i Size { get; private set; }

    public int Count => Size.X * Size.Y * Size.Z;

    public bool IsEmpty => Count == 0;

    // Increments on every Rebuild and Reset, so a consumer can tell whether it is out of date.
    public int Version { get; private set; }

    public void Reset()
    {
        Origin = Vector3i.Zero;
        Size = Vector3i.Zero;
        Version++;
    }

    public byte GetTexel(int x, int y, int z)
    {
        int i = x - Origin.X;
        int j = y - Origin.Y;
        int k = z - Origin.Z;
        if ((uint)i >= (uint)Size.X || (uint)j >= (uint)Size.Y || (uint)k >= (uint)Size.Z)
        {
            return EmptyTexel;
        }

        return _texels[i + (Size.X * (j + (Size.Y * k)))];
    }

    public void Rebuild(IReadOnlyList<StaticVoxelRenderCell> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        Version++;

        // Pass 1: bounds of the full cells.
        int count = cells.Count;
        bool any = false;
        int minX = 0, minY = 0, minZ = 0, maxX = 0, maxY = 0, maxZ = 0;
        for (int n = 0; n < count; n++)
        {
            if (!TryGetFullCell(cells[n], out int x, out int y, out int z))
            {
                continue;
            }

            if (!any)
            {
                any = true;
                minX = maxX = x;
                minY = maxY = y;
                minZ = maxZ = z;
                continue;
            }

            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            minZ = Math.Min(minZ, z);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
            maxZ = Math.Max(maxZ, z);
        }

        long sizeX = (long)maxX - minX + 1;
        long sizeY = (long)maxY - minY + 1;
        long sizeZ = (long)maxZ - minZ + 1;
        if (!any || sizeX > MaxDimension || sizeY > MaxDimension || sizeZ > MaxDimension)
        {
            Origin = Vector3i.Zero;
            Size = Vector3i.Zero;
            return;
        }

        int sx = (int)sizeX, sy = (int)sizeY, sz = (int)sizeZ;
        int total = sx * sy * sz;
        if (_texels.Length < total)
        {
            _texels = new byte[total];
        }
        else
        {
            Array.Clear(_texels, 0, total);
        }

        Origin = new Vector3i(minX, minY, minZ);
        Size = new Vector3i(sx, sy, sz);

        // Pass 2: write the ids.
        byte[] texels = _texels;
        for (int n = 0; n < count; n++)
        {
            StaticVoxelRenderCell cell = cells[n];
            if (!TryGetFullCell(cell, out int x, out int y, out int z))
            {
                continue;
            }

            int id = (int)cell.MaterialId;
            byte texel = id >= 0 && id < BlendableMaterialCount ? (byte)(id + 1) : NonBlendableTexel;
            texels[(x - minX) + (sx * ((y - minY) + (sy * (z - minZ))))] = texel;
        }

        // Pass 3: a blendable cell with any full cell above it is covered.
        int layer = sx;
        int column = sx * sy;
        for (int k = 0; k < sz; k++)
        {
            for (int j = 0; j < sy - 1; j++)
            {
                int row = (k * column) + (j * layer);
                for (int i = 0; i < sx; i++)
                {
                    byte texel = texels[row + i];
                    if (texel >= 1 && texel <= BlendableMaterialCount && texels[row + layer + i] != EmptyTexel)
                    {
                        texels[row + i] = CoveredTexel;
                    }
                }
            }
        }
    }

    // A full cell is one unit cube on the integer grid.
    private static bool TryGetFullCell(in StaticVoxelRenderCell cell, out int x, out int y, out int z)
    {
        x = y = z = 0;
        Vector3 size = cell.Size;
        if (MathF.Abs(size.X - 1f) > FullCellTolerance ||
            MathF.Abs(size.Y - 1f) > FullCellTolerance ||
            MathF.Abs(size.Z - 1f) > FullCellTolerance)
        {
            return false;
        }

        Vector3 min = cell.Min;
        float rx = MathF.Round(min.X);
        float ry = MathF.Round(min.Y);
        float rz = MathF.Round(min.Z);
        if (MathF.Abs(min.X - rx) > FullCellTolerance ||
            MathF.Abs(min.Y - ry) > FullCellTolerance ||
            MathF.Abs(min.Z - rz) > FullCellTolerance ||
            MathF.Abs(rx) > 1e7f || MathF.Abs(ry) > 1e7f || MathF.Abs(rz) > 1e7f)
        {
            return false;
        }

        x = (int)rx;
        y = (int)ry;
        z = (int)rz;
        return true;
    }
}
