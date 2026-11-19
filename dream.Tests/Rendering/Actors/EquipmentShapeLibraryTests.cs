using OpenTK.Mathematics;
using Xunit;
using Dream.Rendering.Actors;
using Dream.Rendering.World;

namespace Dream.Tests.Rendering.Actors;

// Pins the contract stated in EquipmentShape.cs: every sculpted unit mesh stays inside the unit box,
// fills it on at least one axis, is wound and normalled outward, and stays inside its triangle
// budget. An inverted winding or a stray normal is invisible in a diff and loud on screen, so the
// winding gate below (face normal versus vertex normals) is the test that matters most.
public class EquipmentShapeLibraryTests
{
    private const float PositionTolerance = 1e-4f;

    // What each shape is allowed to cost. The figure is 28 x 101 pixels, so density buys nothing.
    private static readonly Dictionary<EquipmentShape, int> TriangleBudget = new()
    {
        [EquipmentShape.Box] = 12,
        [EquipmentShape.BevelBox] = 100,
        // 96: four lathe bands of ten plus two capped ends. Six over the briefed 90, and the only
        // cheaper option is dropping a chamfer or a side, both of which cost more than six triangles
        // are worth.
        [EquipmentShape.Shaft] = 100,
        [EquipmentShape.Taper] = 60,
        [EquipmentShape.Blade] = 160,
        [EquipmentShape.Plate] = 260,
        [EquipmentShape.Horn] = 120,
        [EquipmentShape.Flare] = 200,
        [EquipmentShape.Dome] = 180,
        [EquipmentShape.Spike] = 40,
        [EquipmentShape.Ring] = 230,
        [EquipmentShape.Crescent] = 180,
        [EquipmentShape.Gem] = 60,
        [EquipmentShape.Panel] = 160,

        // The pass-4 profiled shells. An arc shell of S segments and R rings costs
        // (R - 1) * 2 * (S + 1) * 2 for the walls plus 4 * S for the two strip rims, so the ring
        // table is what the budget is really buying: Cuirass has six rings because an hourglass
        // needs a waist AND two flares, Lame has eight because three steps need two rings each.
        [EquipmentShape.Pauldron] = 260,
        [EquipmentShape.Cuirass] = 300,
        [EquipmentShape.Greave] = 260,
        [EquipmentShape.Lame] = 360,
        [EquipmentShape.Membrane] = 330,
    };

    public static IEnumerable<object[]> EveryShape() =>
        Enum.GetValues<EquipmentShape>().Select(shape => new object[] { shape });

    [Theory]
    [MemberData(nameof(EveryShape))]
    public void EveryVertexStaysInsideTheUnitBox(EquipmentShape shape)
    {
        StaticWorldChunkMesh mesh = EquipmentShapeLibrary.Build(shape);
        Assert.NotEmpty(mesh.Vertices);

        foreach (StaticWorldVertex vertex in mesh.Vertices)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                float value = vertex.Position[axis];
                Assert.True(float.IsFinite(value), $"{shape} has a non-finite position on axis {axis}");
                Assert.InRange(value, -0.5f - PositionTolerance, 0.5f + PositionTolerance);
            }
        }
    }

    [Theory]
    [MemberData(nameof(EveryShape))]
    public void TheBoundingBoxFillsTheUnitOnAtLeastOneAxis(EquipmentShape shape)
    {
        StaticWorldChunkMesh mesh = EquipmentShapeLibrary.Build(shape);
        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        foreach (StaticWorldVertex vertex in mesh.Vertices)
        {
            min = Vector3.ComponentMin(min, vertex.Position);
            max = Vector3.ComponentMax(max, vertex.Position);
        }

        bool filled = false;
        for (int axis = 0; axis < 3; axis++)
        {
            filled |= MathF.Abs(min[axis] + 0.5f) <= PositionTolerance
                && MathF.Abs(max[axis] - 0.5f) <= PositionTolerance;
        }

        Assert.True(filled, $"{shape} never touches both -0.5 and +0.5 on any axis: {min} .. {max}");
    }

    [Theory]
    [MemberData(nameof(EveryShape))]
    public void EveryNormalIsUnitLengthAndEveryUvIsInRange(EquipmentShape shape)
    {
        StaticWorldChunkMesh mesh = EquipmentShapeLibrary.Build(shape);

        foreach (StaticWorldVertex vertex in mesh.Vertices)
        {
            Assert.True(
                float.IsFinite(vertex.Normal.X) && float.IsFinite(vertex.Normal.Y) && float.IsFinite(vertex.Normal.Z),
                $"{shape} has a non-finite normal");
            Assert.InRange(vertex.Normal.Length, 1f - 1e-3f, 1f + 1e-3f);

            Assert.True(float.IsFinite(vertex.TexCoord.X) && float.IsFinite(vertex.TexCoord.Y));
            Assert.InRange(vertex.TexCoord.X, -1e-4f, 1f + 1e-4f);
            Assert.InRange(vertex.TexCoord.Y, -1e-4f, 1f + 1e-4f);
        }
    }

    [Theory]
    [MemberData(nameof(EveryShape))]
    public void EveryTriangleIsWholeAndNotDegenerate(EquipmentShape shape)
    {
        StaticWorldChunkMesh mesh = EquipmentShapeLibrary.Build(shape);

        Assert.True(mesh.Indices.Count > 0);
        Assert.Equal(0, mesh.Indices.Count % 3);

        for (int i = 0; i < mesh.Indices.Count; i += 3)
        {
            Vector3 a = VertexAt(mesh, i);
            Vector3 b = VertexAt(mesh, i + 1);
            Vector3 c = VertexAt(mesh, i + 2);
            float area = Vector3.Cross(b - a, c - a).Length * 0.5f;
            Assert.True(area > 1e-9f, $"{shape} has a degenerate triangle at index {i} (area {area})");
        }
    }

    // The gate that catches an inverted face: the triangle's own geometric normal has to agree with
    // the normals its vertices carry, or the lighting runs backwards on that facet.
    [Theory]
    [MemberData(nameof(EveryShape))]
    public void EveryTriangleIsWoundAndNormalledOutward(EquipmentShape shape)
    {
        StaticWorldChunkMesh mesh = EquipmentShapeLibrary.Build(shape);

        for (int i = 0; i < mesh.Indices.Count; i += 3)
        {
            Vector3 a = VertexAt(mesh, i);
            Vector3 b = VertexAt(mesh, i + 1);
            Vector3 c = VertexAt(mesh, i + 2);
            Vector3 face = Vector3.Normalize(Vector3.Cross(b - a, c - a));

            Vector3 average = Vector3.Zero;
            for (int corner = 0; corner < 3; corner++)
            {
                Vector3 normal = mesh.Vertices[(int)mesh.Indices[i + corner]].Normal;
                average += normal;
                Assert.True(
                    Vector3.Dot(face, normal) >= 0f,
                    $"{shape} triangle at index {i} is wound against its corner normal {corner}: "
                    + $"{a} {b} {c} face {face} corner {normal}");
            }

            average = Vector3.Normalize(average);
            Assert.True(
                Vector3.Dot(face, average) >= 0.25f,
                $"{shape} triangle at index {i} disagrees with its averaged normal "
                + $"({Vector3.Dot(face, average)})");
        }
    }

    [Theory]
    [MemberData(nameof(EveryShape))]
    public void EveryShapeStaysInsideItsTriangleBudget(EquipmentShape shape)
    {
        StaticWorldChunkMesh mesh = EquipmentShapeLibrary.Build(shape);

        Assert.True(TriangleBudget.ContainsKey(shape), $"{shape} has no triangle budget");
        Assert.InRange(mesh.TriangleCount, 1, TriangleBudget[shape]);
    }

    // The AABB each shape ACTUALLY occupies, to a hundredth. "Touches both extremes on one axis" is
    // not enough: Plate shipped as a 150-degree arc whose Z ran [0.09, 0.50], so every Plate part was
    // drawn in the front 41% of its authored box with its centroid a third of a box forward of its
    // Center - plate armour that missed the back of the body, invisible to every gate because the
    // gates measure the part's BOX. Pin the real extents so the next off-centre shape shows in a diff.
    //
    // Shapes that fill the box on all three axes are the drop-in replacements for a Box. The ones
    // that do not are listed here with what they really are: Ring is a torus, so only X and Z span
    // the box and Y is the minor diameter; Gem's girdle fills X and Z and its points fill Y.
    [Theory]
    [InlineData(EquipmentShape.Box, -0.50f, -0.50f, -0.50f, 0.50f, 0.50f, 0.50f)]
    [InlineData(EquipmentShape.BevelBox, -0.50f, -0.50f, -0.50f, 0.50f, 0.50f, 0.50f)]
    [InlineData(EquipmentShape.Shaft, -0.50f, -0.50f, -0.50f, 0.50f, 0.50f, 0.50f)]
    [InlineData(EquipmentShape.Taper, -0.50f, -0.50f, -0.50f, 0.50f, 0.50f, 0.50f)]
    [InlineData(EquipmentShape.Plate, -0.50f, -0.50f, -0.50f, 0.50f, 0.50f, 0.50f)]
    [InlineData(EquipmentShape.Dome, -0.50f, -0.50f, -0.50f, 0.50f, 0.50f, 0.50f)]
    [InlineData(EquipmentShape.Spike, -0.50f, -0.50f, -0.50f, 0.50f, 0.50f, 0.50f)]
    [InlineData(EquipmentShape.Gem, -0.50f, -0.50f, -0.50f, 0.50f, 0.50f, 0.50f)]
    [InlineData(EquipmentShape.Crescent, -0.50f, -0.50f, -0.50f, 0.50f, 0.50f, 0.50f)]
    [InlineData(EquipmentShape.Horn, -0.50f, -0.50f, -0.50f, 0.50f, 0.50f, 0.50f)]
    [InlineData(EquipmentShape.Flare, -0.50f, -0.50f, -0.50f, 0.50f, 0.50f, 0.50f)]
    [InlineData(EquipmentShape.Pauldron, -0.50f, -0.50f, -0.50f, 0.50f, 0.50f, 0.50f)]
    [InlineData(EquipmentShape.Cuirass, -0.50f, -0.50f, -0.50f, 0.50f, 0.50f, 0.50f)]
    [InlineData(EquipmentShape.Greave, -0.50f, -0.50f, -0.50f, 0.50f, 0.50f, 0.50f)]
    [InlineData(EquipmentShape.Lame, -0.50f, -0.50f, -0.50f, 0.50f, 0.50f, 0.50f)]
    public void AShapeThatFillsItsBoxReallyFillsIt(
        EquipmentShape shape, float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
    {
        StaticWorldChunkMesh mesh = EquipmentShapeLibrary.Build(shape);
        (Vector3 min, Vector3 max) = Extents(mesh);

        Assert.Equal(minX, min.X, 2);
        Assert.Equal(minY, min.Y, 2);
        Assert.Equal(minZ, min.Z, 2);
        Assert.Equal(maxX, max.X, 2);
        Assert.Equal(maxY, max.Y, 2);
        Assert.Equal(maxZ, max.Z, 2);
    }

    // Panel and Membrane are sheets: they fill X and Y, and their Z is the wall thickness plus the
    // camber that gives cloth its run of light, so it is left unfitted and sits slightly toward -Z.
    // Pinned here because a silent change moves every cloak, tabard and wing off the body.
    [Theory]
    [InlineData(EquipmentShape.Panel)]
    [InlineData(EquipmentShape.Membrane)]
    public void ASheetIsASheetInZAndIsPinnedToWhatItReallyIs(EquipmentShape shape)
    {
        StaticWorldChunkMesh mesh = EquipmentShapeLibrary.Build(shape);
        (Vector3 min, Vector3 max) = Extents(mesh);

        Assert.Equal(-0.50f, min.X, 2);
        Assert.Equal(0.50f, max.X, 2);
        Assert.Equal(-0.50f, min.Y, 2);
        Assert.Equal(0.50f, max.Y, 2);
        Assert.InRange(max.Z - min.Z, 0.20f, 0.40f);
        Assert.InRange(MathF.Abs(max.Z + min.Z) * 0.5f, 0f, 0.10f);
    }

    // Ring is a torus in the XZ plane: it fills X and Z and its Y is the MINOR diameter, which is
    // stated in EquipmentShape.cs. It is the only shape that does not fill all three axes, and it is
    // pinned here so that staying that way is a deliberate choice rather than an accident.
    [Theory]
    [InlineData(EquipmentShape.Ring, 0.30f)]
    public void AShapeThatDoesNotFillYIsPinnedToWhatItReallyIs(EquipmentShape shape, float heightOfBox)
    {
        StaticWorldChunkMesh mesh = EquipmentShapeLibrary.Build(shape);
        (Vector3 min, Vector3 max) = Extents(mesh);

        Assert.Equal(-0.50f, min.X, 2);
        Assert.Equal(0.50f, max.X, 2);
        Assert.Equal(-0.50f, min.Z, 2);
        Assert.Equal(0.50f, max.Z, 2);
        Assert.Equal(heightOfBox, max.Y - min.Y, 2);
        Assert.Equal(0f, max.Y + min.Y, 2);
    }

    // EquipmentShapeLibrary.ShapeCount indexes a per-shape array, so the enum has to be contiguous
    // from zero or EquipmentMeshCache silently hands out the wrong mesh - or a Box - for the gap.
    [Fact]
    public void TheShapeEnumIsContiguousFromZero()
    {
        EquipmentShape[] shapes = Enum.GetValues<EquipmentShape>();

        for (int i = 0; i < shapes.Length; i++)
        {
            Assert.Equal(i, (int)shapes[i]);
        }

        Assert.Equal(shapes.Length, EquipmentShapeLibrary.ShapeCount);
    }

    private static (Vector3 Min, Vector3 Max) Extents(StaticWorldChunkMesh mesh)
    {
        Vector3 min = new(float.MaxValue);
        Vector3 max = new(float.MinValue);
        for (int i = 0; i < mesh.Vertices.Count; i++)
        {
            min = Vector3.ComponentMin(min, mesh.Vertices[i].Position);
            max = Vector3.ComponentMax(max, mesh.Vertices[i].Position);
        }

        return (min, max);
    }

    // Every shape here is closed, so the divergence theorem gives a positive volume. A globally
    // inverted mesh - the one bug the per-triangle gate can miss when it is consistently wrong -
    // comes out negative.
    [Theory]
    [MemberData(nameof(EveryShape))]
    public void EveryShapeEnclosesAPositiveVolume(EquipmentShape shape)
    {
        StaticWorldChunkMesh mesh = EquipmentShapeLibrary.Build(shape);

        double volume = 0d;
        for (int i = 0; i < mesh.Indices.Count; i += 3)
        {
            Vector3 a = VertexAt(mesh, i);
            Vector3 b = VertexAt(mesh, i + 1);
            Vector3 c = VertexAt(mesh, i + 2);
            volume += Vector3.Dot(a, Vector3.Cross(b - a, c - a)) / 6d;
        }

        Assert.True(volume > 1e-4d, $"{shape} encloses no volume ({volume}), so it is inside out or open");
    }

    [Theory]
    [MemberData(nameof(EveryShape))]
    public void BuildIsDeterministic(EquipmentShape shape)
    {
        StaticWorldChunkMesh first = EquipmentShapeLibrary.Build(shape);
        StaticWorldChunkMesh second = EquipmentShapeLibrary.Build(shape);

        Assert.Equal(first.Vertices.Count, second.Vertices.Count);
        for (int i = 0; i < first.Vertices.Count; i++)
        {
            Assert.Equal(first.Vertices[i], second.Vertices[i]);
        }

        Assert.Equal(first.Indices, second.Indices);
    }

    [Fact]
    public void BoxIsStillTheLegacyUnitCube()
    {
        StaticWorldChunkMesh mesh = EquipmentShapeLibrary.Build(EquipmentShape.Box);

        Assert.Equal(12, mesh.TriangleCount);
        Assert.Equal(24, mesh.Vertices.Count);

        HashSet<Vector3> normals = [];
        foreach (StaticWorldVertex vertex in mesh.Vertices)
        {
            foreach (int axis in new[] { 0, 1, 2 })
            {
                Assert.InRange(MathF.Abs(vertex.Position[axis]), 0.5f - 1e-5f, 0.5f + 1e-5f);
            }

            normals.Add(vertex.Normal);
        }

        Assert.Equal(
            new HashSet<Vector3>
            {
                -Vector3.UnitX, Vector3.UnitX, -Vector3.UnitY, Vector3.UnitY, -Vector3.UnitZ, Vector3.UnitZ,
            },
            normals);
    }

    [Fact]
    public void EveryShapeIsBuilt()
    {
        Assert.Equal(Enum.GetValues<EquipmentShape>().Length, EquipmentShapeLibrary.ShapeCount);

        // No two shapes may collapse onto the same mesh: a fall-through to the box is a missing shape.
        StaticWorldChunkMesh box = EquipmentShapeLibrary.Build(EquipmentShape.Box);
        foreach (EquipmentShape shape in Enum.GetValues<EquipmentShape>())
        {
            if (shape == EquipmentShape.Box)
            {
                continue;
            }

            StaticWorldChunkMesh mesh = EquipmentShapeLibrary.Build(shape);
            Assert.True(
                mesh.Vertices.Count != box.Vertices.Count || !mesh.Vertices.SequenceEqual(box.Vertices),
                $"{shape} still falls through to the unit cube");
        }
    }

    private static Vector3 VertexAt(StaticWorldChunkMesh mesh, int index)
    {
        uint vertex = mesh.Indices[index];
        Assert.InRange(vertex, 0u, (uint)mesh.Vertices.Count - 1u);
        return mesh.Vertices[(int)vertex].Position;
    }
}
