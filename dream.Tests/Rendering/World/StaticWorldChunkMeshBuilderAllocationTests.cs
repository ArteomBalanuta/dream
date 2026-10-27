using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dream.Rendering.Core;
using Dream.Rendering.World;
using Dream.Simulation.World.Geometry;
using Dream.Tests.Utilities;
using OpenTK.Mathematics;
using Xunit;
using Xunit.Abstractions;

namespace Dream.Tests.Rendering.World;

public class StaticWorldChunkMeshBuilderAllocationTests
{
    // Steady state is about 200 B on top of the vertex/index storage: the two List objects, two
    // array headers and the mesh record. The rest is headroom for pool noise when tests run in parallel.
    private const int MeshObjectOverheadBudgetBytes = 1024;
    private const int MeasuredBuilds = 100;
    private readonly ITestOutputHelper _output;

    public StaticWorldChunkMeshBuilderAllocationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static TheoryData<int, StaticTerrainMaterialRole> Layouts()
    {
        TheoryData<int, StaticTerrainMaterialRole> data = new();
        int[] seeds = [1, 7, 42, 1337, 90210];
        foreach (int seed in seeds)
        {
            data.Add(seed, StaticTerrainMaterialRole.Single);
        }

        data.Add(42, StaticTerrainMaterialRole.Top);
        data.Add(42, StaticTerrainMaterialRole.Side);
        data.Add(42, StaticTerrainMaterialRole.Bottom);
        return data;
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void BuildMatchesOriginalAlgorithmVertexAndIndexOrder(int seed, StaticTerrainMaterialRole role)
    {
        ChunkLayout layout = CreateLayout(seed);

        StaticWorldChunkMesh expected = ReferenceBuilder.Build(
            new StaticWorldChunkKey(1, 0, 2),
            GeometryMaterialId.Grass,
            role,
            layout.MaterialCells,
            layout.OccluderCells);
        StaticWorldChunkMesh actual = StaticWorldChunkMeshBuilder.Build(
            new StaticWorldChunkKey(1, 0, 2),
            GeometryMaterialId.Grass,
            role,
            layout.MaterialCells,
            layout.OccluderCells);

        Assert.True(expected.Vertices.Count > 0);
        Assert.Equal(expected.ExposedFaceCount, actual.ExposedFaceCount);
        Assert.Equal(expected.SourceCellCount, actual.SourceCellCount);
        Assert.Equal(expected.Bounds, actual.Bounds);
        Assert.Equal(expected.Vertices.Count, actual.Vertices.Count);
        for (int i = 0; i < expected.Vertices.Count; i++)
        {
            StaticWorldVertex e = expected.Vertices[i];
            StaticWorldVertex a = actual.Vertices[i];
            Assert.True(BitEquals(e, a), $"Vertex {i} differs: expected {e}, actual {a}.");
        }

        Assert.Equal(expected.Indices, actual.Indices);
    }

    [Fact]
    public void BuildMatchesOriginalAlgorithmForSingleMaterialOverload()
    {
        ChunkLayout layout = CreateLayout(5);

        StaticWorldChunkMesh expected = ReferenceBuilder.Build(
            new StaticWorldChunkKey(0, 0, 0),
            GeometryMaterialId.Stone,
            StaticTerrainMaterialRole.Single,
            layout.MaterialCells,
            layout.MaterialCells);
        StaticWorldChunkMesh actual = StaticWorldChunkMeshBuilder.Build(
            new StaticWorldChunkKey(0, 0, 0),
            GeometryMaterialId.Stone,
            layout.MaterialCells);

        Assert.Equal(expected.Vertices, actual.Vertices);
        Assert.Equal(expected.Indices, actual.Indices);
        Assert.Equal(expected.ExposedFaceCount, actual.ExposedFaceCount);
    }

    [Fact]
    public void BuildHandlesEmptyCells()
    {
        StaticWorldChunkMesh mesh = StaticWorldChunkMeshBuilder.Build(
            new StaticWorldChunkKey(0, 0, 0),
            GeometryMaterialId.Grass,
            StaticTerrainMaterialRole.Single,
            Array.Empty<StaticVoxelRenderCell>(),
            Array.Empty<StaticVoxelRenderCell>());

        Assert.Empty(mesh.Vertices);
        Assert.Empty(mesh.Indices);
        Assert.Equal(0, mesh.ExposedFaceCount);
    }

    [Fact]
    public void BuildAllocatesOnlyTheFinalVertexAndIndexStorage()
    {
        ChunkLayout layout = CreateLayout(42);
        StaticWorldChunkKey key = new(0, 0, 0);
        StaticWorldChunkMesh mesh = StaticWorldChunkMeshBuilder.Build(
            key,
            GeometryMaterialId.Grass,
            StaticTerrainMaterialRole.Single,
            layout.MaterialCells,
            layout.OccluderCells);
        long storageBytes =
            ((long)mesh.Vertices.Count * 32) +
            ((long)mesh.Indices.Count * sizeof(uint));

        long bytesPerBuild = AllocationAssert.MeasureBytesPerIteration(() =>
            StaticWorldChunkMeshBuilder.Build(
                key,
                GeometryMaterialId.Grass,
                StaticTerrainMaterialRole.Single,
                layout.MaterialCells,
                layout.OccluderCells),
            MeasuredBuilds);

        _output.WriteLine(
            $"cells={layout.MaterialCells.Length} occluders={layout.OccluderCells.Length} " +
            $"faces={mesh.ExposedFaceCount} vertices={mesh.Vertices.Count} indices={mesh.Indices.Count} " +
            $"storage={storageBytes} B bytesPerBuild={bytesPerBuild} B");
        Assert.True(
            bytesPerBuild <= storageBytes + MeshObjectOverheadBudgetBytes,
            $"Allocated {bytesPerBuild} B per build; final storage is {storageBytes} B.");
    }

    [Fact]
    public void BuildReturnsListsSizedExactlyForUpload()
    {
        ChunkLayout layout = CreateLayout(7);

        StaticWorldChunkMesh mesh = StaticWorldChunkMeshBuilder.Build(
            new StaticWorldChunkKey(0, 0, 0),
            GeometryMaterialId.Grass,
            StaticTerrainMaterialRole.Single,
            layout.MaterialCells,
            layout.OccluderCells);

        List<StaticWorldVertex> vertices = Assert.IsType<List<StaticWorldVertex>>(mesh.Vertices);
        List<uint> indices = Assert.IsType<List<uint>>(mesh.Indices);
        Assert.Equal(mesh.ExposedFaceCount * 4, vertices.Capacity);
        Assert.Equal(mesh.ExposedFaceCount * 6, indices.Capacity);
    }

    [Fact]
    public void VertexLayoutMatchesGlAttributeOffsets()
    {
        StaticMeshBuffer.VerifyVertexLayout();

        Assert.Equal(StaticMeshBuffer.VertexStride, Unsafe.SizeOf<StaticWorldVertex>());
        Assert.Equal(0, StaticMeshBuffer.PositionOffset);
        Assert.Equal(12, StaticMeshBuffer.NormalOffset);
        Assert.Equal(24, StaticMeshBuffer.TexCoordOffset);

        StaticWorldVertex[] vertices =
        [
            new(new Vector3(1f, 2f, 3f), new Vector3(4f, 5f, 6f), new Vector2(7f, 8f)),
            new(new Vector3(9f, 10f, 11f), new Vector3(12f, 13f, 14f), new Vector2(15f, 16f))
        ];
        ReadOnlySpan<float> floats = MemoryMarshal.Cast<StaticWorldVertex, float>(vertices);
        Assert.Equal(16, floats.Length);
        for (int i = 0; i < floats.Length; i++)
        {
            Assert.Equal(i + 1f, floats[i]);
        }
    }

    private static bool BitEquals(StaticWorldVertex a, StaticWorldVertex b)
    {
        return BitEquals(a.Position.X, b.Position.X) &&
            BitEquals(a.Position.Y, b.Position.Y) &&
            BitEquals(a.Position.Z, b.Position.Z) &&
            BitEquals(a.Normal.X, b.Normal.X) &&
            BitEquals(a.Normal.Y, b.Normal.Y) &&
            BitEquals(a.Normal.Z, b.Normal.Z) &&
            BitEquals(a.TexCoord.X, b.TexCoord.X) &&
            BitEquals(a.TexCoord.Y, b.TexCoord.Y);
    }

    private static bool BitEquals(float a, float b)
    {
        return BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
    }

    // Terrain-like chunk: a 16x16 heightfield of unit grass cells over stone, with some
    // half-size and wide slab cells so partial and multi-cell occlusion paths are exercised.
    private static ChunkLayout CreateLayout(int seed)
    {
        Random random = new(seed);
        List<StaticVoxelRenderCell> material = new();
        List<StaticVoxelRenderCell> occluders = new();
        for (int x = 0; x < 16; x++)
        {
            for (int z = 0; z < 16; z++)
            {
                int height = random.Next(1, 6);
                for (int y = 0; y < height; y++)
                {
                    bool grass = y == height - 1 || random.Next(4) == 0;
                    GeometryMaterialId materialId = grass ? GeometryMaterialId.Grass : GeometryMaterialId.Stone;
                    StaticVoxelRenderCell cell = new(
                        new Vector3(x + 0.5f, y + 0.5f, z + 0.5f),
                        Vector3.One,
                        materialId);
                    occluders.Add(cell);
                    if (grass)
                    {
                        material.Add(cell);
                    }
                }

                if (random.Next(10) == 0)
                {
                    StaticVoxelRenderCell half = new(
                        new Vector3(x + 0.25f, height + 0.25f, z + 0.25f),
                        new Vector3(0.5f),
                        GeometryMaterialId.Grass);
                    material.Add(half);
                    occluders.Add(half);
                }
            }
        }

        for (int i = 0; i < 6; i++)
        {
            float x = random.Next(0, 14);
            float z = random.Next(0, 14);
            StaticVoxelRenderCell slab = new(
                new Vector3(x + 1f, 7.25f, z + 1f),
                new Vector3(2f, 0.5f, 2f),
                GeometryMaterialId.Grass);
            material.Add(slab);
            occluders.Add(slab);
        }

        return new ChunkLayout(material.ToArray(), occluders.ToArray());
    }

    private sealed record ChunkLayout(
        StaticVoxelRenderCell[] MaterialCells,
        StaticVoxelRenderCell[] OccluderCells);

    // Copy of the builder as it was before MEM-8, kept as the golden reference for
    // vertex/index order and coverage decisions.
    private static class ReferenceBuilder
    {
        private const float Epsilon = 0.0001f;

        public static StaticWorldChunkMesh Build(
            StaticWorldChunkKey key,
            GeometryMaterialId materialId,
            StaticTerrainMaterialRole materialRole,
            IReadOnlyList<StaticVoxelRenderCell> materialCells,
            IReadOnlyList<StaticVoxelRenderCell> occluderCells)
        {
            List<StaticWorldVertex> vertices = new();
            List<uint> indices = new();
            int exposedFaceCount = 0;
            Dictionary<int, List<StaticVoxelRenderCell>>[] index = CreateIndex(occluderCells);

            foreach (StaticVoxelRenderCell cell in materialCells)
            {
                for (int direction = 0; direction < 6; direction++)
                {
                    Vector3 normal = Normals[direction];
                    if (materialRole != StaticTerrainMaterialRole.Single &&
                        StaticTerrainMaterialCatalog.ResolveRoleForNormal(materialId, normal) != materialRole)
                    {
                        continue;
                    }

                    if (IsFaceCovered(cell, direction, index))
                    {
                        continue;
                    }

                    uint firstIndex = (uint)vertices.Count;
                    Vector3[] positions = GetPositions(cell, direction);
                    Vector2[] texCoords = GetTexCoords(cell, direction);
                    for (int corner = 0; corner < 4; corner++)
                    {
                        vertices.Add(new StaticWorldVertex(positions[corner], normal, texCoords[corner]));
                    }

                    indices.Add(firstIndex);
                    indices.Add(firstIndex + 1);
                    indices.Add(firstIndex + 2);
                    indices.Add(firstIndex);
                    indices.Add(firstIndex + 2);
                    indices.Add(firstIndex + 3);
                    exposedFaceCount++;
                }
            }

            return new StaticWorldChunkMesh(
                key,
                materialId,
                materialRole,
                vertices,
                indices,
                StaticWorldChunkBounds.FromCells(materialCells),
                materialCells.Count,
                exposedFaceCount);
        }

        private static readonly Vector3[] Normals =
        [
            Vector3.UnitX,
            -Vector3.UnitX,
            Vector3.UnitY,
            -Vector3.UnitY,
            Vector3.UnitZ,
            -Vector3.UnitZ
        ];

        private static Dictionary<int, List<StaticVoxelRenderCell>>[] CreateIndex(
            IReadOnlyList<StaticVoxelRenderCell> occluderCells)
        {
            Dictionary<int, List<StaticVoxelRenderCell>>[] index = new Dictionary<int, List<StaticVoxelRenderCell>>[6];
            for (int i = 0; i < 6; i++)
            {
                index[i] = new Dictionary<int, List<StaticVoxelRenderCell>>();
            }

            foreach (StaticVoxelRenderCell occluder in occluderCells)
            {
                Add(index[0], occluder.Min.X, occluder);
                Add(index[1], occluder.Max.X, occluder);
                Add(index[2], occluder.Min.Y, occluder);
                Add(index[3], occluder.Max.Y, occluder);
                Add(index[4], occluder.Min.Z, occluder);
                Add(index[5], occluder.Max.Z, occluder);
            }

            return index;
        }

        private static void Add(Dictionary<int, List<StaticVoxelRenderCell>> index, float plane, StaticVoxelRenderCell occluder)
        {
            int key = Quantize(plane);
            if (!index.TryGetValue(key, out List<StaticVoxelRenderCell>? candidates))
            {
                candidates = new List<StaticVoxelRenderCell>();
                index[key] = candidates;
            }

            candidates.Add(occluder);
        }

        private static bool IsFaceCovered(
            StaticVoxelRenderCell cell,
            int direction,
            Dictionary<int, List<StaticVoxelRenderCell>>[] index)
        {
            float plane = direction switch
            {
                0 => cell.Max.X,
                1 => cell.Min.X,
                2 => cell.Max.Y,
                3 => cell.Min.Y,
                4 => cell.Max.Z,
                _ => cell.Min.Z
            };
            int key = Quantize(plane);
            for (int offset = -1; offset <= 1; offset++)
            {
                if (!index[direction].TryGetValue(key + offset, out List<StaticVoxelRenderCell>? candidates))
                {
                    continue;
                }

                foreach (StaticVoxelRenderCell occluder in candidates)
                {
                    if (CoversFace(cell, occluder, direction))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool CoversFace(StaticVoxelRenderCell cell, StaticVoxelRenderCell occluder, int direction)
        {
            return direction switch
            {
                0 => NearlyEqual(occluder.Min.X, cell.Max.X) &&
                    Covers(occluder.Min.Y, occluder.Max.Y, cell.Min.Y, cell.Max.Y) &&
                    Covers(occluder.Min.Z, occluder.Max.Z, cell.Min.Z, cell.Max.Z),
                1 => NearlyEqual(occluder.Max.X, cell.Min.X) &&
                    Covers(occluder.Min.Y, occluder.Max.Y, cell.Min.Y, cell.Max.Y) &&
                    Covers(occluder.Min.Z, occluder.Max.Z, cell.Min.Z, cell.Max.Z),
                2 => NearlyEqual(occluder.Min.Y, cell.Max.Y) &&
                    Covers(occluder.Min.X, occluder.Max.X, cell.Min.X, cell.Max.X) &&
                    Covers(occluder.Min.Z, occluder.Max.Z, cell.Min.Z, cell.Max.Z),
                3 => NearlyEqual(occluder.Max.Y, cell.Min.Y) &&
                    Covers(occluder.Min.X, occluder.Max.X, cell.Min.X, cell.Max.X) &&
                    Covers(occluder.Min.Z, occluder.Max.Z, cell.Min.Z, cell.Max.Z),
                4 => NearlyEqual(occluder.Min.Z, cell.Max.Z) &&
                    Covers(occluder.Min.X, occluder.Max.X, cell.Min.X, cell.Max.X) &&
                    Covers(occluder.Min.Y, occluder.Max.Y, cell.Min.Y, cell.Max.Y),
                _ => NearlyEqual(occluder.Max.Z, cell.Min.Z) &&
                    Covers(occluder.Min.X, occluder.Max.X, cell.Min.X, cell.Max.X) &&
                    Covers(occluder.Min.Y, occluder.Max.Y, cell.Min.Y, cell.Max.Y)
            };
        }

        private static bool Covers(float candidateMin, float candidateMax, float targetMin, float targetMax)
        {
            return candidateMin <= targetMin + Epsilon &&
                candidateMax >= targetMax - Epsilon;
        }

        private static bool NearlyEqual(float a, float b)
        {
            return MathF.Abs(a - b) <= Epsilon;
        }

        private static int Quantize(float value)
        {
            return (int)MathF.Round(value / Epsilon);
        }

        private static Vector3[] GetPositions(StaticVoxelRenderCell cell, int direction)
        {
            Vector3 min = cell.Min;
            Vector3 max = cell.Max;
            return direction switch
            {
                0 => [new(max.X, min.Y, min.Z), new(max.X, max.Y, min.Z), new(max.X, max.Y, max.Z), new(max.X, min.Y, max.Z)],
                1 => [new(min.X, min.Y, max.Z), new(min.X, max.Y, max.Z), new(min.X, max.Y, min.Z), new(min.X, min.Y, min.Z)],
                2 => [new(min.X, max.Y, max.Z), new(max.X, max.Y, max.Z), new(max.X, max.Y, min.Z), new(min.X, max.Y, min.Z)],
                3 => [new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z), new(max.X, min.Y, max.Z), new(min.X, min.Y, max.Z)],
                4 => [new(min.X, min.Y, max.Z), new(max.X, min.Y, max.Z), new(max.X, max.Y, max.Z), new(min.X, max.Y, max.Z)],
                _ => [new(max.X, min.Y, min.Z), new(min.X, min.Y, min.Z), new(min.X, max.Y, min.Z), new(max.X, max.Y, min.Z)]
            };
        }

        private static Vector2[] GetTexCoords(StaticVoxelRenderCell cell, int direction)
        {
            Vector3 size = cell.Size;
            return direction switch
            {
                0 or 1 => [new(0f, 0f), new(0f, size.Y), new(size.Z, size.Y), new(size.Z, 0f)],
                2 => [new(0f, size.Z), new(size.X, size.Z), new(size.X, 0f), new(0f, 0f)],
                3 => [new(0f, 0f), new(size.X, 0f), new(size.X, size.Z), new(0f, size.Z)],
                _ => [new(0f, 0f), new(size.X, 0f), new(size.X, size.Y), new(0f, size.Y)]
            };
        }
    }
}
