using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using Dream.Simulation.World.MapSources;
using Dream.Simulation.World.Terrain;
using Xunit;

namespace Dream.Tests.Simulation.World.Terrain;

public class TerrainChunkCodecTests
{
    private static MapDefinition Def(int width = 16, int height = 2, int depth = 16) =>
        new(width, height, depth, WorldMapSource.Generated, "default", 1);

    private static GeometryCellRecord Cube(int x, int y, int z, int res = 1, bool solid = true,
        GeometryMaterialId material = GeometryMaterialId.Grass, GeometrySubdivisionState? state = null) =>
        new(new GeometryCellKey(x, y, z), new GeometryCellDefinition(GeometryShapeKind.SolidCube, material, res, solid, IsFloor: y == 0), state);

    // Offset of the first subdivision record in a one-cell chunk blob of the given height.
    private static int SubdivisionOffset(int height, int cellCount, int paletteCount) =>
        23 + (paletteCount * 4) + (16 * 16 * height / 8) + cellCount;

    private static string Hex(byte[] data) => Convert.ToHexString(data);

    [Fact]
    public void HandBuiltChunkMatchesTheGoldenBlob()
    {
        GeometrySubdivisionState state = new(2);
        state.SetChildSolid(new GeometrySubcellKey(1, 0, 1), false); // child (1*2+0)*2+1 = 5
        byte[] blob = TerrainChunkCodec.Encode(Def(), 0, 0, [Cube(1, 1, 2, res: 2, state: state)]);

        // header, palette, 64 B bitmap (slot (1*16+2)*2+1 = 37 -> byte 4, bit 5), cell byte, subdivision record
        string expected =
            "4454434B" + "0100" + "0000" + "00000000" + "00000000" + "1010" + "0200" + "0100" + "01" +
            "00000201" +
            "00000000" + "20" + new string('0', 2 * 59) +
            "80" +
            "0202" + "0000" + "0020";
        Assert.Equal(98, blob.Length);
        Assert.Equal(expected, Hex(blob));

        GeometryCellRecord decoded = Assert.Single(TerrainChunkCodec.Decode(Def(), 0, 0, blob));
        Assert.Equal(new GeometryCellKey(1, 1, 2), decoded.Key);
        Assert.False(decoded.IsChildSolid(new GeometrySubcellKey(1, 0, 1)));
        Assert.True(decoded.IsChildSolid(new GeometrySubcellKey(0, 0, 0)));
    }

    [Fact]
    public void EmptyChunkRoundTrips()
    {
        byte[] blob = TerrainChunkCodec.Encode(Def(), 0, 0, []);
        Assert.Equal(23 + 64, blob.Length);
        Assert.Empty(TerrainChunkCodec.Decode(Def(), 0, 0, blob));
    }

    [Theory]
    [InlineData(2, 1)] // 2 + 2*2 = 6 < 8 B bitmask: list mode
    [InlineData(3, 0)] // 2 + 2*3 = 8 == 8 B bitmask: tie keeps the bitmask
    [InlineData(4, 0)] // longer than the bitmask
    [InlineData(0, 0)] // empty set: 3 B list vs 8 B bitmask, so the list wins below (see assertion)
    public void SetModeFollowsTheShortestRuleAndTiesKeepTheBitmask(int count, int expectedMode)
    {
        // Resolution 4 on an empty cube: 64 children, 8 B bitmask.
        GeometrySubdivisionState state = new(4);
        for (int i = 0; i < count; i++)
        {
            state.SetChildSolid(new GeometrySubcellKey(0, 0, i), true);
        }

        byte[] blob = TerrainChunkCodec.Encode(Def(), 0, 0, [Cube(0, 1, 0, res: 4, solid: false, state: state)]);
        int offset = SubdivisionOffset(2, 1, 1);
        Assert.Equal(4, blob[offset]);
        if (count == 0)
        {
            expectedMode = 1;
        }

        Assert.Equal(expectedMode, blob[offset + 2]);
        int setLength = expectedMode == 0 ? 1 + 8 : 1 + 2 + (2 * count);
        Assert.Equal(offset + 2 + setLength, blob.Length);

        GeometryCellRecord decoded = Assert.Single(TerrainChunkCodec.Decode(Def(), 0, 0, blob));
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(i < count, decoded.IsChildSolid(new GeometrySubcellKey(0, 0, i)));
        }
    }

    [Fact]
    public void AllEmptyAndFalseOverrideSubdivisionsRoundTripByteIdentically()
    {
        GeometrySubdivisionState rubble = new(4);
        rubble.SetAllChildrenEmpty();
        rubble.SetChildSolid(new GeometrySubcellKey(2, 2, 2), true);
        GeometrySubdivisionState carved = new(4);
        carved.SetChildSolid(new GeometrySubcellKey(3, 3, 3), false);
        carved.SetChildSolid(new GeometrySubcellKey(1, 0, 0), false);
        GeometryCellRecord[] cells =
        [
            Cube(0, 0, 0),
            Cube(0, 1, 0, res: 4, solid: false, state: rubble),
            Cube(5, 1, 7, res: 4, state: carved),
            Cube(5, 0, 7, material: GeometryMaterialId.Stone),
        ];

        byte[] first = TerrainChunkCodec.Encode(Def(), 0, 0, cells);
        List<GeometryCellRecord> decoded = TerrainChunkCodec.Decode(Def(), 0, 0, first);
        byte[] second = TerrainChunkCodec.Encode(Def(), 0, 0, decoded);

        Assert.Equal(Hex(first), Hex(second));
        Assert.Equal(4, decoded.Count);
        Assert.True(decoded.Select(c => c.Key).SequenceEqual(
            [new(0, 0, 0), new(0, 1, 0), new(5, 0, 7), new(5, 1, 7)]));
        Assert.True(decoded[1].SubdivisionState!.AllChildrenEmpty);
        Assert.True(decoded[1].IsChildSolid(new GeometrySubcellKey(2, 2, 2)));
        Assert.False(decoded[1].IsChildSolid(new GeometrySubcellKey(0, 0, 0)));
        Assert.False(decoded[3].IsChildSolid(new GeometrySubcellKey(3, 3, 3)));
        Assert.True(decoded[3].IsChildSolid(new GeometrySubcellKey(3, 3, 2)));
    }

    [Fact]
    public void EncodeRejectsUnencodableRecords()
    {
        GeometrySubdivisionState outOfRange = new(2);
        outOfRange.SetChildSolid(new GeometrySubcellKey(2, 0, 0), true);
        Assert.Throws<InvalidOperationException>(() =>
            TerrainChunkCodec.Encode(Def(), 0, 0, [Cube(0, 1, 0, res: 2, state: outOfRange)]));

        GeometryCellRecord withPieces = Cube(0, 1, 0);
        withPieces.SetFracturePieces([]);
        Assert.Throws<InvalidOperationException>(() => TerrainChunkCodec.Encode(Def(), 0, 0, [withPieces]));

        Assert.Throws<InvalidOperationException>(() => TerrainChunkCodec.Encode(Def(), 0, 0, [Cube(16, 0, 0)]));
        Assert.Throws<InvalidOperationException>(() => TerrainChunkCodec.Encode(Def(), 0, 0, [Cube(0, 2, 0)]));
        Assert.Throws<InvalidOperationException>(() => TerrainChunkCodec.Encode(Def(), 0, 0, [Cube(0, 0, 0), Cube(0, 0, 0)]));

        // 128 distinct definitions in one chunk: resolutions 1..16 x 8 materials.
        MapDefinition tall = Def(height: 200);
        List<GeometryCellRecord> many = [];
        int y = 0;
        for (int res = 1; res <= 16; res++)
        {
            foreach (GeometryMaterialId material in Enum.GetValues<GeometryMaterialId>())
            {
                many.Add(Cube(0, y++, 0, res: res, material: material));
            }
        }

        Assert.Throws<InvalidOperationException>(() => TerrainChunkCodec.Encode(tall, 0, 0, many));
    }

    [Fact]
    public void EdgeChunkOutOfBoundsCellsAreRejectedOnEncode()
    {
        MapDefinition def = Def(width: 20, depth: 20);
        Assert.Throws<InvalidOperationException>(() => TerrainChunkCodec.Encode(def, 1, 0, [Cube(20, 0, 0)]));
        byte[] blob = TerrainChunkCodec.Encode(def, 1, 1, [Cube(19, 0, 19)]);
        Assert.Single(TerrainChunkCodec.Decode(def, 1, 1, blob));
    }

    private static byte[] Golden()
    {
        GeometrySubdivisionState state = new(2);
        state.SetChildSolid(new GeometrySubcellKey(1, 0, 1), false);
        return TerrainChunkCodec.Encode(Def(), 0, 0, [Cube(1, 1, 2, res: 2, state: state)]);
    }

    private static byte[] Patched(Action<byte[]> patch)
    {
        byte[] blob = Golden();
        patch(blob);
        return blob;
    }

    private static void AssertRejected(byte[] blob, MapDefinition? def = null, int chunkX = 0, int chunkZ = 0)
    {
        Assert.Throws<InvalidDataException>(() => TerrainChunkCodec.Decode(def ?? Def(), chunkX, chunkZ, blob));
    }

    [Fact]
    public void DecodeRejectsHeaderViolations()
    {
        AssertRejected(Patched(b => b[0] = (byte)'X'));
        AssertRejected(Patched(b => b[4] = 2));
        AssertRejected(Patched(b => b[6] = 1));
        AssertRejected(Patched(b => b[8] = 1));
        AssertRejected(Golden(), chunkZ: 1);
        AssertRejected(Patched(b => b[16] = 8));
        AssertRejected(Patched(b => b[18] = 3)); // height differs from the world
        AssertRejected(Patched(b => b[20] = 2)); // cell count differs from the occupancy bits
        AssertRejected(Patched(b => b[22] = 0)); // palette count 0 with a cell
        AssertRejected(Patched(b => b[22] = 128)); // palette overflow
        AssertRejected(Golden().AsSpan(0, 10).ToArray());
        AssertRejected(Golden().AsSpan(0, 90).ToArray());
    }

    [Fact]
    public void DecodeRejectsPaletteAndFlagViolations()
    {
        AssertRejected(Patched(b => b[23] = 9)); // unknown shape
        AssertRejected(Patched(b => b[24] = 99)); // unknown material
        AssertRejected(Patched(b => b[25] = 0)); // resolution 0
        AssertRejected(Patched(b => b[26] = 4)); // reserved palette flag bit
        AssertRejected(Patched(b => b[26] = 4 | 1));
    }

    [Fact]
    public void DecodeRejectsTrailingBytesAndOutOfBoundsBits()
    {
        AssertRejected([.. Golden(), 0]);

        // Edge chunk of a 20-wide world: local x 4 is world x 20, outside the bounds.
        MapDefinition def = Def(width: 20, depth: 20);
        byte[] edge = TerrainChunkCodec.Encode(def, 1, 0, [Cube(16, 0, 0)]);
        byte[] bad = (byte[])edge.Clone();
        int bitmapStart = 23 + 4;
        bad[bitmapStart] = 0; // clear the in-bounds bit (slot 0)
        int slot = (4 * 16 * 2); // local x = 4, z = 0, y = 0
        bad[bitmapStart + (slot >> 3)] |= (byte)(1 << (slot & 7));
        AssertRejected(bad, def, 1, 0);
        // The same blob is fine against a wider world.
        Assert.Single(TerrainChunkCodec.Decode(Def(width: 32, depth: 20), 1, 0, bad));
    }

    [Fact]
    public void DecodeRejectsNonCanonicalSubdivisionRecords()
    {
        // Resolution 4 on an empty cube; the golden header/palette/bitmap, then a hand-written record.
        GeometrySubdivisionState state = new(4);
        byte[] baseBlob = TerrainChunkCodec.Encode(Def(), 0, 0, [Cube(0, 1, 0, res: 4, solid: false, state: state)]);
        int offset = SubdivisionOffset(2, 1, 1);
        byte[] Record(params byte[] tail) => [.. baseBlob.AsSpan(0, offset).ToArray(), .. tail];

        // Canonical: three children in bitmask mode (tie), no false set.
        Assert.Single(TerrainChunkCodec.Decode(Def(), 0, 0, Record(4, 0, 0, 0b0000_0111, 0, 0, 0, 0, 0, 0, 0)));
        // The same set in list mode is not the shortest-with-tie-to-bitmask form.
        AssertRejected(Record(4, 0, 1, 3, 0, 0, 0, 1, 0, 2, 0));
        // Empty set as an 8 B bitmask when the 3 B list is shorter.
        AssertRejected(Record(4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
        // List entries must ascend and stay in range.
        AssertRejected(Record(4, 0, 1, 2, 0, 5, 0, 5, 0));
        AssertRejected(Record(4, 0, 1, 1, 0, 64, 0));
        // Unknown mode, unknown flag bits, resolution that differs from the palette.
        AssertRejected(Record(4, 0, 2, 0, 0));
        AssertRejected(Record(4, 4, 1, 0, 0));
        AssertRejected(Record(5, 0, 1, 0, 0));
        // Bitmask bits beyond the child count cannot occur at res 4 (64 children); res 3 has 27.
        // False-override flag with an empty false set, and a false override on an already-empty child.
        AssertRejected(Record(4, 2, 1, 0, 0, 1, 0, 0));
        AssertRejected(Record(4, 2, 1, 0, 0, 1, 1, 0, 3, 0));
        // False overrides on an all-empty state.
        AssertRejected(Record(4, 3, 1, 0, 0, 1, 1, 0, 3, 0));
    }

    [Fact]
    public void DecodeRejectsRedundantTrueOverridesAndNonFirstAppearancePalettes()
    {
        // A true override on a child the solid cube already has.
        GeometrySubdivisionState state = new(2);
        byte[] solid = TerrainChunkCodec.Encode(Def(), 0, 0, [Cube(0, 1, 0, res: 2, state: state)]);
        int offset = SubdivisionOffset(2, 1, 1);
        AssertRejected([.. solid.AsSpan(0, offset).ToArray(), 2, 0, 1, 1, 0, 0, 0]);

        // Two palette entries listed in reverse order of first appearance, and one that is never used.
        byte[] two = TerrainChunkCodec.Encode(Def(), 0, 0, [Cube(0, 0, 0), Cube(0, 1, 0, material: GeometryMaterialId.Stone)]);
        byte[] swapped = (byte[])two.Clone();
        for (int i = 0; i < 4; i++)
        {
            (swapped[23 + i], swapped[27 + i]) = (swapped[27 + i], swapped[23 + i]);
        }

        Assert.Equal(2, TerrainChunkCodec.Decode(Def(), 0, 0, two).Count);
        // Swapping the palette entries leaves cell bytes 0,1 pointing at them in appearance order: still
        // canonical (just different materials). Reversing the cell bytes instead breaks first-appearance.
        int cellBytes = 23 + 8 + 64;
        byte[] reversed = (byte[])two.Clone();
        (reversed[cellBytes], reversed[cellBytes + 1]) = (reversed[cellBytes + 1], reversed[cellBytes]);
        AssertRejected(reversed);

        byte[] unused = (byte[])two.Clone();
        unused[cellBytes + 1] = 0;
        AssertRejected(unused);
    }
}
