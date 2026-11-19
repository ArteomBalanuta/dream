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
        [EquipmentShape.Plate] = 220,
        [EquipmentShape.Dome] = 180,
        [EquipmentShape.Spike] = 40,
        [EquipmentShape.Ring] = 230,
        [EquipmentShape.Crescent] = 180,
        [EquipmentShape.Gem] = 60,
        [EquipmentShape.Panel] = 160,
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
