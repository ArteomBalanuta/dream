using System.Buffers.Binary;
using Dream.Simulation.World.Geometry;
using Dream.Simulation.World.MapSources;

namespace Dream.Simulation.World.Terrain;

// Encodes and decodes one terrain chunk blob ("DTCK" v1, see docs/ledger/plans/2026-10-29-wave-d-plan.md).
// Little-endian, uncompressed and canonical: one record set has exactly one byte representation, and the
// decoder throws InvalidDataException on anything the encoder would not have written. Encode and decode
// are exact inverses at the level of effective records (key, definition, effective child solidity).
public static class TerrainChunkCodec
{
    private const int HeaderSize = 23;
    private const int MaxPalette = 127;
    private const int MaxResolution = 40;
    private const int MaxHeight = 1024;
    private const int Size = TerrainChunkFormat.ChunkSize;
    private static readonly byte[] Magic = [(byte)'D', (byte)'T', (byte)'C', (byte)'K'];

    // Number of chunk columns a definition spans.
    public static int ChunkCountX(MapDefinition definition) => (definition.Width + Size - 1) / Size;

    public static int ChunkCountZ(MapDefinition definition) => (definition.Depth + Size - 1) / Size;

    // The cells must all lie inside chunk (chunkX, chunkZ) and inside the definition's bounds, in any order.
    public static byte[] Encode(MapDefinition definition, int chunkX, int chunkZ, IReadOnlyList<GeometryCellRecord> cells)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(cells);
        int height = definition.Height;
        if (height is < 1 or > MaxHeight)
        {
            throw new InvalidOperationException($"height {height} is outside 1..{MaxHeight}");
        }

        if (cells.Count > ushort.MaxValue)
        {
            throw new InvalidOperationException($"{cells.Count} cells exceed the 16-bit chunk cell count ({ushort.MaxValue})");
        }

        int slotCount = Size * Size * height;
        int[] slotOfCell = new int[cells.Count];
        int[] order = new int[cells.Count];
        HashSet<int> seen = [];
        for (int n = 0; n < cells.Count; n++)
        {
            GeometryCellRecord cell = cells[n];
            int slot = SlotOf(definition, chunkX, chunkZ, cell.Key);
            if (!seen.Add(slot))
            {
                throw new InvalidOperationException($"duplicate cell {cell.Key}");
            }

            if (cell.FracturePieces != null)
            {
                throw new InvalidOperationException($"cell {cell.Key} has fracture pieces");
            }

            slotOfCell[n] = slot;
            order[n] = n;
        }

        Array.Sort(slotOfCell, order);

        List<PaletteEntry> palette = [];
        Dictionary<PaletteEntry, int> paletteIndex = [];
        byte[] cellBytes = new byte[cells.Count];
        MemoryStream subdivisions = new();
        byte[] bitmap = new byte[(slotCount + 7) / 8];
        for (int n = 0; n < order.Length; n++)
        {
            GeometryCellRecord cell = cells[order[n]];
            int slot = slotOfCell[n];
            bitmap[slot >> 3] |= (byte)(1 << (slot & 7));
            GeometryCellDefinition d = cell.Definition;
            if ((cell.Key.Y == 0) != d.IsFloor)
            {
                throw new InvalidOperationException($"cell {cell.Key}: the floor flag must be set exactly at y == 0");
            }

            if (!Enum.IsDefined(d.ShapeKind) || !Enum.IsDefined(d.MaterialId) ||
                d.SubdivisionResolution is < 1 or > MaxResolution)
            {
                throw new InvalidOperationException($"cell {cell.Key} has an unencodable definition");
            }

            PaletteEntry entry = new(
                (byte)d.ShapeKind,
                (byte)d.MaterialId,
                (byte)d.SubdivisionResolution,
                (byte)((d.IsSolid ? 1 : 0) | (d.IsFloor ? 2 : 0)));
            if (!paletteIndex.TryGetValue(entry, out int index))
            {
                index = palette.Count;
                if (index >= MaxPalette)
                {
                    throw new InvalidOperationException("more than 127 distinct cell definitions in one chunk");
                }

                palette.Add(entry);
                paletteIndex.Add(entry, index);
            }

            byte b = (byte)index;
            if (cell.SubdivisionState != null)
            {
                b |= 0x80;
                WriteSubdivision(subdivisions, cell, cell.SubdivisionState);
            }

            cellBytes[n] = b;
        }

        using MemoryStream output = new();
        Span<byte> header = stackalloc byte[HeaderSize];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], TerrainChunkFormat.CurrentVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], chunkX);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], chunkZ);
        header[16] = Size;
        header[17] = Size;
        BinaryPrimitives.WriteUInt16LittleEndian(header[18..], (ushort)height);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], (ushort)cells.Count);
        header[22] = (byte)palette.Count;
        output.Write(header);
        foreach (PaletteEntry p in palette)
        {
            output.WriteByte(p.Shape);
            output.WriteByte(p.Material);
            output.WriteByte(p.Resolution);
            output.WriteByte(p.Flags);
        }

        output.Write(bitmap);
        output.Write(cellBytes);
        subdivisions.Position = 0;
        subdivisions.CopyTo(output);
        return output.ToArray();
    }

    // Decodes into records in ascending local-slot order, which is (x, z, y) ascending inside the chunk.
    public static List<GeometryCellRecord> Decode(MapDefinition definition, int chunkX, int chunkZ, ReadOnlySpan<byte> data)
    {
        long unlimited = long.MaxValue;
        return Decode(definition, chunkX, chunkZ, data, ref unlimited);
    }

    // As above, for untrusted input: each override set's entry count is subtracted from the budget BEFORE
    // the set is allocated, and InvalidDataException is thrown when the budget would drop below zero.
    public static List<GeometryCellRecord> Decode(
        MapDefinition definition, int chunkX, int chunkZ, ReadOnlySpan<byte> data, ref long subcellOverrideBudget)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (chunkX < 0 || chunkZ < 0 || chunkX >= ChunkCountX(definition) || chunkZ >= ChunkCountZ(definition))
        {
            throw Bad("chunk coordinates are outside the world");
        }

        int height = definition.Height;
        if (data.Length < HeaderSize || !data[..4].SequenceEqual(Magic))
        {
            throw Bad("bad magic or truncated header");
        }

        Reader r = new(data);
        r.Skip(4);
        if (r.U16() != TerrainChunkFormat.CurrentVersion)
        {
            throw Bad("unsupported format version");
        }

        if (r.U16() != 0)
        {
            throw Bad("reserved field is not zero");
        }

        if (r.I32() != chunkX || r.I32() != chunkZ)
        {
            throw Bad("chunk coordinates differ from the row");
        }

        if (r.U8() != Size || r.U8() != Size)
        {
            throw Bad("unsupported chunk size");
        }

        int blobHeight = r.U16();
        if (blobHeight is < 1 or > MaxHeight || blobHeight != height)
        {
            throw Bad("height differs from the world");
        }

        int slotCount = Size * Size * height;
        int cellCount = r.U16();
        if (cellCount > slotCount)
        {
            throw Bad("cell count exceeds the chunk volume");
        }

        int paletteCount = r.U8();
        if (paletteCount > MaxPalette || (paletteCount == 0) != (cellCount == 0))
        {
            throw Bad("bad palette count");
        }

        GeometryCellDefinition[] palette = new GeometryCellDefinition[paletteCount];
        HashSet<(byte, byte, byte, byte)> distinct = [];
        for (int p = 0; p < paletteCount; p++)
        {
            byte shape = r.U8();
            byte material = r.U8();
            byte res = r.U8();
            byte flags = r.U8();
            if (!Enum.IsDefined((GeometryShapeKind)shape) || !Enum.IsDefined((GeometryMaterialId)material) ||
                res is < 1 or > MaxResolution || (flags & ~3) != 0 || !distinct.Add((shape, material, res, flags)))
            {
                throw Bad("bad palette entry");
            }

            palette[p] = new GeometryCellDefinition(
                (GeometryShapeKind)shape,
                (GeometryMaterialId)material,
                res,
                (flags & 1) != 0,
                (flags & 2) != 0);
        }

        ReadOnlySpan<byte> bitmap = r.Take((slotCount + 7) / 8);
        int[] slots = new int[cellCount];
        int found = 0;
        for (int slot = 0; slot < slotCount; slot++)
        {
            if ((bitmap[slot >> 3] & (1 << (slot & 7))) == 0)
            {
                continue;
            }

            if (found >= cellCount)
            {
                throw Bad("more occupied slots than cells");
            }

            GeometryCellKey key = KeyOf(chunkX, chunkZ, height, slot);
            if (key.X >= definition.Width || key.Z >= definition.Depth)
            {
                throw Bad("occupancy bit outside the world bounds");
            }

            slots[found++] = slot;
        }

        if (found != cellCount)
        {
            throw Bad("fewer occupied slots than cells");
        }

        ReadOnlySpan<byte> cellBytes = r.Take(cellCount);
        int nextNewPalette = 0;
        for (int n = 0; n < cellCount; n++)
        {
            int index = cellBytes[n] & 0x7F;
            if (index > nextNewPalette || index >= paletteCount)
            {
                throw Bad("palette indices are not in first-appearance order");
            }

            if (index == nextNewPalette)
            {
                nextNewPalette++;
            }
        }

        if (nextNewPalette != paletteCount)
        {
            throw Bad("unused palette entry");
        }

        List<GeometryCellRecord> result = new(cellCount);
        for (int n = 0; n < cellCount; n++)
        {
            GeometryCellDefinition d = palette[cellBytes[n] & 0x7F];
            GeometryCellKey key = KeyOf(chunkX, chunkZ, height, slots[n]);
            if ((key.Y == 0) != d.IsFloor)
            {
                throw Bad("the floor flag must be set exactly at y == 0");
            }

            GeometrySubdivisionState? state = (cellBytes[n] & 0x80) != 0 ? ReadSubdivision(ref r, d, ref subcellOverrideBudget) : null;
            result.Add(new GeometryCellRecord(key, d, state));
        }

        if (!r.AtEnd)
        {
            throw Bad("trailing bytes");
        }

        return result;
    }

    private static int SlotOf(MapDefinition definition, int chunkX, int chunkZ, GeometryCellKey key)
    {
        int lx = key.X - (chunkX * Size);
        int lz = key.Z - (chunkZ * Size);
        if ((uint)lx >= Size || (uint)lz >= Size || key.X >= definition.Width || key.Z >= definition.Depth ||
            (uint)key.Y >= (uint)definition.Height)
        {
            throw new InvalidOperationException($"cell {key} is outside chunk ({chunkX}, {chunkZ}) or the world");
        }

        return (((lx * Size) + lz) * definition.Height) + key.Y;
    }

    private static GeometryCellKey KeyOf(int chunkX, int chunkZ, int height, int slot)
    {
        int y = slot % height;
        int xz = slot / height;
        return new GeometryCellKey((chunkX * Size) + (xz / Size), y, (chunkZ * Size) + (xz % Size));
    }

    private static void WriteSubdivision(MemoryStream output, GeometryCellRecord cell, GeometrySubdivisionState state)
    {
        int res = state.Resolution;
        if (res != cell.Definition.SubdivisionResolution)
        {
            throw new InvalidOperationException($"cell {cell.Key} subdivision resolution differs from its definition");
        }

        int n = res * res * res;
        bool allEmpty = state.AllChildrenEmpty;
        List<int> trues = [];
        List<int> falses = [];
        int inRangeOverrides = 0;
        int c = 0;
        for (int x = 0; x < res; x++)
        {
            for (int y = 0; y < res; y++)
            {
                for (int z = 0; z < res; z++, c++)
                {
                    GeometrySubcellKey child = new(x, y, z);
                    if (!state.TryGetChildSolid(child, out bool solid))
                    {
                        continue;
                    }

                    inRangeOverrides++;
                    if (allEmpty)
                    {
                        if (solid)
                        {
                            trues.Add(c);
                        }

                        continue;
                    }

                    // Overrides equal to the shape's own occupancy carry no information; they are not written.
                    bool baseSolid = GeometryShapeOccupancy.IsChildSolid(cell.Definition, child);
                    if (solid && !baseSolid)
                    {
                        trues.Add(c);
                    }
                    else if (!solid && baseSolid)
                    {
                        falses.Add(c);
                    }
                }
            }
        }

        if (state.OverrideCount != inRangeOverrides)
        {
            throw new InvalidOperationException($"cell {cell.Key} has child overrides outside [0, {res})");
        }

        output.WriteByte((byte)res);
        output.WriteByte((byte)((allEmpty ? 1 : 0) | (falses.Count > 0 ? 2 : 0)));
        WriteSet(output, trues, n);
        if (falses.Count > 0)
        {
            WriteSet(output, falses, n);
        }
    }

    // Mode 1 (list) only when strictly shorter than the bitmask; a tie keeps mode 0.
    private static bool UseListMode(int count, int n) => 2 + (2 * count) < (n + 7) / 8;

    private static void WriteSet(MemoryStream output, List<int> ascending, int n)
    {
        if (UseListMode(ascending.Count, n))
        {
            output.WriteByte(1);
            output.WriteByte((byte)ascending.Count);
            output.WriteByte((byte)(ascending.Count >> 8));
            foreach (int c in ascending)
            {
                output.WriteByte((byte)c);
                output.WriteByte((byte)(c >> 8));
            }

            return;
        }

        output.WriteByte(0);
        byte[] mask = new byte[(n + 7) / 8];
        foreach (int c in ascending)
        {
            mask[c >> 3] |= (byte)(1 << (c & 7));
        }

        output.Write(mask);
    }

    private static GeometrySubdivisionState ReadSubdivision(ref Reader r, GeometryCellDefinition d, ref long budget)
    {
        int res = r.U8();
        if (res != d.SubdivisionResolution)
        {
            throw Bad("subdivision resolution differs from the palette");
        }

        int flags = r.U8();
        if ((flags & ~3) != 0)
        {
            throw Bad("bad subdivision flags");
        }

        bool allEmpty = (flags & 1) != 0;
        bool hasFalse = (flags & 2) != 0;
        if (allEmpty && hasFalse)
        {
            throw Bad("false overrides on an all-empty subdivision");
        }

        int n = res * res * res;
        List<int> trues = ReadSet(ref r, n, ref budget);
        List<int> falses = hasFalse ? ReadSet(ref r, n, ref budget) : [];
        if (hasFalse && falses.Count == 0)
        {
            throw Bad("false-override flag set with no false overrides");
        }

        GeometrySubdivisionState state = new(res);
        if (allEmpty)
        {
            state.SetAllChildrenEmpty();
        }

        foreach (int c in trues)
        {
            GeometrySubcellKey child = ChildOf(c, res);
            if (!allEmpty && GeometryShapeOccupancy.IsChildSolid(d, child))
            {
                throw Bad("redundant true override");
            }

            state.SetChildSolid(child, true);
        }

        foreach (int c in falses)
        {
            GeometrySubcellKey child = ChildOf(c, res);
            if (!GeometryShapeOccupancy.IsChildSolid(d, child))
            {
                throw Bad("redundant false override");
            }

            state.SetChildSolid(child, false);
        }

        return state;
    }

    private static GeometrySubcellKey ChildOf(int c, int res)
    {
        return new GeometrySubcellKey(c / (res * res), (c / res) % res, c % res);
    }

    private static void Spend(ref long budget, int count)
    {
        if (count > budget)
        {
            throw Bad("subcell override budget exceeded");
        }

        budget -= count;
    }

    private static List<int> ReadSet(ref Reader r, int n, ref long budget)
    {
        int mode = r.U8();
        if (mode == 0)
        {
            ReadOnlySpan<byte> mask = r.Take((n + 7) / 8);
            int set = 0;
            for (int c = 0; c < n; c++)
            {
                if ((mask[c >> 3] & (1 << (c & 7))) != 0)
                {
                    set++;
                }
            }

            Spend(ref budget, set);
            List<int> result = new(set);
            for (int c = 0; c < n; c++)
            {
                if ((mask[c >> 3] & (1 << (c & 7))) != 0)
                {
                    result.Add(c);
                }
            }

            if ((n & 7) != 0 && (mask[^1] >> (n & 7)) != 0)
            {
                throw Bad("bitmask has bits beyond the child count");
            }

            if (UseListMode(result.Count, n))
            {
                throw Bad("set is not in its shortest mode");
            }

            return result;
        }

        if (mode != 1)
        {
            throw Bad("unknown set mode");
        }

        int count = r.U16();
        if (!UseListMode(count, n))
        {
            throw Bad("set is not in its shortest mode");
        }

        Spend(ref budget, count);
        List<int> list = new(count);
        int previous = -1;
        for (int i = 0; i < count; i++)
        {
            int c = r.U16();
            if (c <= previous || c >= n)
            {
                throw Bad("set entries are not ascending and in range");
            }

            previous = c;
            list.Add(c);
        }

        return list;
    }

    private static InvalidDataException Bad(string message) => new("terrain chunk: " + message);

    private readonly record struct PaletteEntry(byte Shape, byte Material, byte Resolution, byte Flags);

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _pos;

        public readonly bool AtEnd => _pos == _data.Length;

        public ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || _data.Length - _pos < count)
            {
                throw Bad("truncated data");
            }

            ReadOnlySpan<byte> slice = _data.Slice(_pos, count);
            _pos += count;
            return slice;
        }

        public void Skip(int count) => Take(count);

        public byte U8() => Take(1)[0];

        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    }
}
