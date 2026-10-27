using System.Text.RegularExpressions;
using Dream.Rendering.Core;
using Dream.Rendering.World;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.World;

// Cubes without a model asset (Sand, Mud, Snow, Rock, GrassPath, or any cube when visual
// assets are off) are drawn with this mesh. The old position-only VAO left the cube shader's
// normal and UV attributes disabled, so those cubes rendered as flat, untextured boxes.
public class TerrainFallbackCubeMeshTests
{
    [Fact]
    public void Create_BuildsSixFullyTexturedFaces()
    {
        StaticWorldChunkMesh mesh = TerrainFallbackCubeMesh.Create();

        Assert.Equal(6, mesh.ExposedFaceCount);
        Assert.Equal(24, mesh.Vertices.Count);
        Assert.Equal(36, mesh.Indices.Count);

        foreach (IGrouping<Vector3, StaticWorldVertex> face in mesh.Vertices.GroupBy(vertex => vertex.Normal))
        {
            Vector2[] uvs = face.Select(vertex => vertex.TexCoord).ToArray();
            Assert.Equal(0f, uvs.Min(uv => uv.X), 4);
            Assert.Equal(1f, uvs.Max(uv => uv.X), 4);
            Assert.Equal(0f, uvs.Min(uv => uv.Y), 4);
            Assert.Equal(1f, uvs.Max(uv => uv.Y), 4);
        }
    }

    [Fact]
    public void Create_IsUnitCubeCenteredOnOrigin()
    {
        StaticWorldChunkMesh mesh = TerrainFallbackCubeMesh.Create();

        Assert.All(mesh.Vertices, vertex =>
        {
            Assert.Equal(0.5f, MathF.Abs(vertex.Position.X), 4);
            Assert.Equal(0.5f, MathF.Abs(vertex.Position.Y), 4);
            Assert.Equal(0.5f, MathF.Abs(vertex.Position.Z), 4);
            Assert.Equal(1f, vertex.Normal.Length, 4);
        });
    }

    [Fact]
    public void Create_TrianglesWindCounterClockwiseAroundOutwardNormals()
    {
        // The fallback draw enables back-face culling, so every face must be front-facing from outside.
        StaticWorldChunkMesh mesh = TerrainFallbackCubeMesh.Create();

        for (int index = 0; index < mesh.Indices.Count; index += 3)
        {
            StaticWorldVertex a = mesh.Vertices[(int)mesh.Indices[index]];
            StaticWorldVertex b = mesh.Vertices[(int)mesh.Indices[index + 1]];
            StaticWorldVertex c = mesh.Vertices[(int)mesh.Indices[index + 2]];
            Vector3 windingNormal = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
            Vector3 centroid = (a.Position + b.Position + c.Position) / 3f;

            Assert.True(Vector3.Dot(windingNormal, a.Normal) > 0f, $"Triangle {index / 3} winds against its normal {a.Normal}.");
            Assert.True(Vector3.Dot(centroid, a.Normal) > 0f, $"Triangle {index / 3} normal {a.Normal} points inward.");
        }
    }

    [Fact]
    public void StaticMeshBufferAttributeLocations_MatchCubeShaderInputs()
    {
        string shader = File.ReadAllText(Path.Combine(FindRepoRoot(), "dream", "shaders", "cubeVertex.glsl"));

        Assert.Equal(StaticMeshBuffer.PositionAttributeLocation, ReadAttributeLocation(shader, "aPos"));
        Assert.Equal(StaticMeshBuffer.NormalAttributeLocation, ReadAttributeLocation(shader, "aNormal"));
        Assert.Equal(StaticMeshBuffer.TexCoordAttributeLocation, ReadAttributeLocation(shader, "aTexCoord0"));
    }

    private static int ReadAttributeLocation(string shader, string attributeName)
    {
        Match match = Regex.Match(
            shader,
            $@"layout\s*\(\s*location\s*=\s*(\d+)\s*\)\s*in\s+\w+\s+{attributeName}\s*;");
        Assert.True(match.Success, $"Attribute {attributeName} not found in cubeVertex.glsl.");
        return int.Parse(match.Groups[1].Value);
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "dream")) &&
                Directory.Exists(Path.Combine(current.FullName, "dream.Tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
