using Dream.Rendering.World;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using System.Diagnostics;
using Xunit;

namespace Dream.Tests.Rendering.World;

public class StaticWorldChunkMeshBuilderTests
{
    [Fact]
    public void BuildEmitsSixFacesForSingleCube()
    {
        StaticVoxelRenderCell[] cells =
        [
            new(new Vector3(0.5f, 0.5f, 0.5f), Vector3.One, GeometryMaterialId.Grass)
        ];

        StaticWorldChunkMesh mesh = StaticWorldChunkMeshBuilder.Build(
            new StaticWorldChunkKey(0, 0, 0),
            GeometryMaterialId.Grass,
            cells);

        Assert.Equal(1, mesh.SourceCellCount);
        Assert.Equal(6, mesh.ExposedFaceCount);
        Assert.Equal(24, mesh.Vertices.Count);
        Assert.Equal(36, mesh.Indices.Count);
        Assert.Equal(12, mesh.TriangleCount);
    }

    [Fact]
    public void BuildSuppressesSharedFaceForAdjacentCubes()
    {
        StaticVoxelRenderCell[] cells =
        [
            new(new Vector3(0.5f, 0.5f, 0.5f), Vector3.One, GeometryMaterialId.Grass),
            new(new Vector3(1.5f, 0.5f, 0.5f), Vector3.One, GeometryMaterialId.Grass)
        ];

        StaticWorldChunkMesh mesh = StaticWorldChunkMeshBuilder.Build(
            new StaticWorldChunkKey(0, 0, 0),
            GeometryMaterialId.Grass,
            cells);

        Assert.Equal(2, mesh.SourceCellCount);
        Assert.Equal(10, mesh.ExposedFaceCount);
        Assert.Equal(40, mesh.Vertices.Count);
        Assert.Equal(60, mesh.Indices.Count);
        Assert.Equal(20, mesh.TriangleCount);
    }

    [Fact]
    public void BuildSuppressesInteriorFacesAcrossDifferentMaterials()
    {
        StaticVoxelRenderCell[] grassCells =
        [
            new(new Vector3(0.5f, 0.5f, 0.5f), Vector3.One, GeometryMaterialId.Grass)
        ];
        StaticVoxelRenderCell[] allCells =
        [
            grassCells[0],
            new(new Vector3(1.5f, 0.5f, 0.5f), Vector3.One, GeometryMaterialId.Stone)
        ];

        StaticWorldChunkMesh mesh = StaticWorldChunkMeshBuilder.Build(
            new StaticWorldChunkKey(0, 0, 0),
            GeometryMaterialId.Grass,
            grassCells,
            allCells);

        Assert.Equal(5, mesh.ExposedFaceCount);
    }

    [Fact]
    public void BuildUsesSequentialIndicesInsideVertexRange()
    {
        StaticVoxelRenderCell[] cells =
        [
            new(new Vector3(0.5f, 0.5f, 0.5f), Vector3.One, GeometryMaterialId.Grass)
        ];

        StaticWorldChunkMesh mesh = StaticWorldChunkMeshBuilder.Build(
            new StaticWorldChunkKey(0, 0, 0),
            GeometryMaterialId.Grass,
            cells);

        uint maxIndex = mesh.Indices.Max();

        Assert.True(maxIndex < mesh.Vertices.Count);
        Assert.All(mesh.Indices, index => Assert.True(index < mesh.Vertices.Count));
    }

    [Fact]
    public void ExtractorIncludesCompatibilityCubesAndGeometryBlocks()
    {
        Cube cube = new(isEmpty: false, isSolid: true)
        {
            Position = new Vector3(0.5f, 0.5f, 0.5f),
            Animation = null!,
            ShaderName = "default"
        };
        GeometryRenderBlockDescriptor block = new(
            new GeometryCellKey(1, 0, 0),
            new Vector3(1.5f, 0.5f, 0.5f),
            1f,
            GeometryMaterialId.Stone,
            Animation: null,
            ShaderName: "cube");
        GeometryRenderExport export = new(
            DirtyCells: [],
            Cells: [],
            Blocks: [block],
            CompatibilityCubes: [cube]);

        IReadOnlyList<StaticVoxelRenderCell> cells = StaticWorldChunkExtractor.ExtractCells(export);

        Assert.Equal(2, cells.Count);
        Assert.Contains(cells, cell => cell.MaterialId == GeometryMaterialId.Grass);
        Assert.Contains(cells, cell => cell.MaterialId == GeometryMaterialId.Stone);
    }

    [Fact]
    public void BuildDenseGridCompletesWithinInteractiveBudget()
    {
        const int side = 56;
        StaticVoxelRenderCell[] cells = Enumerable
            .Range(0, side)
            .SelectMany(x => Enumerable
                .Range(0, side)
                .Select(z => new StaticVoxelRenderCell(
                    new Vector3(x + 0.5f, 0.5f, z + 0.5f),
                    Vector3.One,
                    GeometryMaterialId.Grass)))
            .ToArray();

        Stopwatch stopwatch = Stopwatch.StartNew();
        StaticWorldChunkMesh mesh = StaticWorldChunkMeshBuilder.Build(
            new StaticWorldChunkKey(0, 0, 0),
            GeometryMaterialId.Grass,
            StaticTerrainMaterialRole.Top,
            cells,
            cells);
        stopwatch.Stop();

        Assert.Equal(side * side, mesh.ExposedFaceCount);
        Assert.True(
            stopwatch.ElapsedMilliseconds < 250,
            $"Dense grid mesh build took {stopwatch.ElapsedMilliseconds} ms.");
    }
}
