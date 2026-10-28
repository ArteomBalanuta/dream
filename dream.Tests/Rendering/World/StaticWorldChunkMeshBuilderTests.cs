using Dream.Rendering.World;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using System.Diagnostics;
using Xunit;

namespace Dream.Tests.Rendering.World;

[Collection(TimingSensitiveCollection.Name)]
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
    public void BuildTilesSideFaceUvsHorizontallyAndVerticallyInWorldUnits()
    {
        StaticVoxelRenderCell[] cells =
        [
            new(new Vector3(1f, 1.5f, 2f), new Vector3(2f, 3f, 4f), GeometryMaterialId.Dirt)
        ];

        StaticWorldChunkMesh mesh = StaticWorldChunkMeshBuilder.Build(
            new StaticWorldChunkKey(0, 0, 0),
            GeometryMaterialId.Dirt,
            cells);
        StaticWorldVertex[] positiveXFace = mesh.Vertices
            .Where(vertex => vertex.Normal == Vector3.UnitX)
            .ToArray();

        Assert.Equal(4, positiveXFace.Length);
        Assert.Equal(0f, positiveXFace[0].TexCoord.Y);
        Assert.Equal(3f, positiveXFace[1].TexCoord.Y);
        Assert.Equal(3f, positiveXFace[2].TexCoord.Y);
        Assert.Equal(0f, positiveXFace[3].TexCoord.Y);
        Assert.Equal(0f, positiveXFace.Min(vertex => vertex.TexCoord.X));
        Assert.Equal(4f, positiveXFace.Max(vertex => vertex.TexCoord.X));
    }

    [Fact]
    public void ExtractorIncludesCompatibilityCubesAndGeometryBlocks()
    {
        Cube cube = new(isEmpty: false, isSolid: true)
        {
            Position = new Vector3(0.5f, 0.5f, 0.5f),
            ShaderName = "default"
        };
        GeometryRenderBlockDescriptor block = new(
            new GeometryCellKey(1, 0, 0),
            new Vector3(1.5f, 0.5f, 0.5f),
            1f,
            GeometryMaterialId.Stone,
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
    public void ExtractorPreservesCompatibilityCubeMaterialFromGeometryCell()
    {
        Cube cube = new(isEmpty: false, isSolid: true)
        {
            Position = new Vector3(2.5f, 0.5f, 3.5f),
            ShaderName = "default"
        };
        GeometryCellRecord sandCell = new(
            new GeometryCellKey(2, 0, 3),
            new GeometryCellDefinition(
                GeometryShapeKind.SolidCube,
                GeometryMaterialId.Sand,
                SubdivisionResolution: 1,
                IsSolid: true,
                IsFloor: true));
        GeometryRenderExport export = new(
            DirtyCells: [],
            Cells: [new GeometryRenderCell(sandCell.Key, sandCell, cube)],
            Blocks: [],
            CompatibilityCubes: [cube]);

        IReadOnlyList<StaticVoxelRenderCell> cells = StaticWorldChunkExtractor.ExtractCells(export);

        StaticVoxelRenderCell renderCell = Assert.Single(cells);
        Assert.Equal(GeometryMaterialId.Sand, renderCell.MaterialId);
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
