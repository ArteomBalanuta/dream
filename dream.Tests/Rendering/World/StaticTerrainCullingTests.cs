using Dream.Rendering.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Integration;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.World;

// The static terrain pass culls back faces. That is only correct if every mesh face is wound
// counter-clockwise seen from outside (GL's default FrontFace) and nothing in the product flips it.
public class StaticTerrainCullingTests
{
    private static StaticWorldChunkMesh BuildCubeMesh()
    {
        StaticVoxelRenderCell[] cells =
        [
            new(new Vector3(0.5f, 0.5f, 0.5f), Vector3.One, GeometryMaterialId.Grass),
            new(new Vector3(4.5f, 0.5f, 0.5f), new Vector3(1f, 2f, 3f), GeometryMaterialId.Grass),
        ];
        return StaticWorldChunkMeshBuilder.Build(new StaticWorldChunkKey(0, 0, 0), GeometryMaterialId.Grass, cells);
    }

    // Number of triangles whose geometric normal disagrees with their vertex normals.
    private static int CountMiswoundTriangles(IReadOnlyList<StaticWorldVertex> vertices, IReadOnlyList<uint> indices)
    {
        int bad = 0;
        for (int i = 0; i + 2 < indices.Count; i += 3)
        {
            StaticWorldVertex a = vertices[(int)indices[i]];
            StaticWorldVertex b = vertices[(int)indices[i + 1]];
            StaticWorldVertex c = vertices[(int)indices[i + 2]];
            Vector3 geometric = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
            if (Vector3.Dot(geometric, a.Normal) <= 0f)
            {
                bad++;
            }
        }

        return bad;
    }

    [Fact]
    public void EveryTriangleIsCounterClockwiseSeenFromOutside()
    {
        StaticWorldChunkMesh mesh = BuildCubeMesh();

        Assert.Equal(12 * 2, mesh.TriangleCount);
        Assert.Equal(0, CountMiswoundTriangles(mesh.Vertices, mesh.Indices));
    }

    [Fact]
    public void AReversedTriangleFailsTheWindingCheck()
    {
        StaticWorldChunkMesh mesh = BuildCubeMesh();
        uint[] indices = mesh.Indices.ToArray();
        (indices[1], indices[2]) = (indices[2], indices[1]);

        Assert.Equal(1, CountMiswoundTriangles(mesh.Vertices, indices));
    }

    [Fact]
    public void NoProductionFileChangesTheFrontFace()
    {
        string root = Path.Combine(RepoRoot.Find(), "dream");
        List<string> offenders = new();
        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string normalized = file.Replace('\\', '/');
            if (normalized.Contains("/obj/") || normalized.Contains("/bin/"))
            {
                continue;
            }

            if (File.ReadAllText(file).Contains("GL.FrontFace", StringComparison.Ordinal))
            {
                offenders.Add(normalized);
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void BeginEnablesBackFaceCullingAndEndDisablesIt()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot.Find(), "dream", "Rendering", "Core", "Render.Terrain.cs"));

        string begin = Slice(source, "public void BeginStaticTerrainPass", "public void BindStaticTerrainMaterial");
        Assert.Contains("GL.Enable(EnableCap.CullFace);", begin);
        Assert.Contains("GL.CullFace(TriangleFace.Back);", begin);
        Assert.DoesNotContain("GL.Disable(EnableCap.CullFace)", begin);

        string end = Slice(source, "public static void EndStaticTerrainPass", "IWorldInstanceDrawTarget");
        Assert.Contains("GL.Disable(EnableCap.CullFace);", end);
    }

    [Fact]
    public void TheSliceHelperFailsWhenAMarkerIsMissing()
    {
        Assert.ThrowsAny<Exception>(() => Slice("abc", "nope", "c"));
    }

    private static string Slice(string source, string start, string end)
    {
        int from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"missing {start}");
        int to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"missing {end}");
        return source[from..to];
    }
}
