using Dream.Rendering.World;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Rendering.World;

public class StaticWorldChunkRendererTests
{
    [Fact]
    public void PlanGroupsMeshesByChunkAndMaterial()
    {
        StaticVoxelRenderCell[] cells =
        [
            new(new Vector3(0.5f, 0.5f, 0.5f), Vector3.One, GeometryMaterialId.Grass),
            new(new Vector3(1.5f, 0.5f, 0.5f), Vector3.One, GeometryMaterialId.Stone)
        ];

        IReadOnlyList<StaticWorldChunkMesh> meshes = StaticWorldChunkRenderer.PlanMeshes(cells);

        Assert.Equal(4, meshes.Count);
        Assert.Contains(meshes, mesh => mesh.MaterialId == GeometryMaterialId.Grass);
        Assert.Contains(meshes, mesh => mesh.MaterialId == GeometryMaterialId.Stone);
    }

    [Fact]
    public void PlanKeepsAdjacentDifferentMaterialsAsOccluders()
    {
        StaticVoxelRenderCell[] cells =
        [
            new(new Vector3(0.5f, 0.5f, 0.5f), Vector3.One, GeometryMaterialId.Grass),
            new(new Vector3(1.5f, 0.5f, 0.5f), Vector3.One, GeometryMaterialId.Stone)
        ];

        int exposedGrassFaces = StaticWorldChunkRenderer
            .PlanMeshes(cells)
            .Where(mesh => mesh.MaterialId == GeometryMaterialId.Grass)
            .Sum(mesh => mesh.ExposedFaceCount);

        Assert.Equal(5, exposedGrassFaces);
    }

    [Fact]
    public void PlanSplitsGrassCubeIntoTopSideAndBottomTextureMeshes()
    {
        StaticVoxelRenderCell[] cells =
        [
            new(new Vector3(0.5f, 0.5f, 0.5f), Vector3.One, GeometryMaterialId.Grass)
        ];

        IReadOnlyList<StaticWorldChunkMesh> meshes = StaticWorldChunkRenderer.PlanMeshes(cells);

        StaticWorldChunkMesh top = meshes.Single(mesh => mesh.MaterialRole == StaticTerrainMaterialRole.Top);
        StaticWorldChunkMesh side = meshes.Single(mesh => mesh.MaterialRole == StaticTerrainMaterialRole.Side);
        StaticWorldChunkMesh bottom = meshes.Single(mesh => mesh.MaterialRole == StaticTerrainMaterialRole.Bottom);
        Assert.Equal(1, top.ExposedFaceCount);
        Assert.Equal(4, side.ExposedFaceCount);
        Assert.Equal(1, bottom.ExposedFaceCount);
    }

    [Fact]
    public void PlanBuildInputsKeepsExistingChunkSignaturesWhenFarChunkEntersVisibleExport()
    {
        StaticVoxelRenderCell original = new(
            new Vector3(0.5f, 0.5f, 0.5f),
            Vector3.One,
            GeometryMaterialId.Grass);
        StaticVoxelRenderCell far = new(
            new Vector3(48.5f, 0.5f, 0.5f),
            Vector3.One,
            GeometryMaterialId.Stone);

        StaticWorldChunkBuildInput before = StaticWorldChunkRenderer
            .PlanBuildInputs([original])
            .Single(input => input.Key.ChunkKey == new StaticWorldChunkKey(0, 0, 0) &&
                input.Key.MaterialId == GeometryMaterialId.Grass &&
                input.Key.MaterialRole == StaticTerrainMaterialRole.Top);
        StaticWorldChunkBuildInput after = StaticWorldChunkRenderer
            .PlanBuildInputs([original, far])
            .Single(input => input.Key == before.Key);

        Assert.Equal(before.Signature, after.Signature);
    }

    [Fact]
    public void PlanBuildInputsUsesOnlyNeighborChunksAsOccluders()
    {
        StaticVoxelRenderCell local = new(
            new Vector3(0.5f, 0.5f, 0.5f),
            Vector3.One,
            GeometryMaterialId.Grass);
        StaticVoxelRenderCell neighbor = new(
            new Vector3(1.5f, 0.5f, 0.5f),
            Vector3.One,
            GeometryMaterialId.Stone);
        StaticVoxelRenderCell far = new(
            new Vector3(48.5f, 0.5f, 0.5f),
            Vector3.One,
            GeometryMaterialId.Stone);

        StaticWorldChunkBuildInput input = StaticWorldChunkRenderer
            .PlanBuildInputs([local, neighbor, far])
            .Single(input => input.Key.ChunkKey == new StaticWorldChunkKey(0, 0, 0) &&
                input.Key.MaterialId == GeometryMaterialId.Grass &&
                input.Key.MaterialRole == StaticTerrainMaterialRole.Top);

        Assert.Equal(2, input.OccluderCells.Count);
        Assert.DoesNotContain(input.OccluderCells, cell => cell.Center == far.Center);
    }

    [Fact]
    public void PlanBuildInputsCanSkipAlreadyCachedChunks()
    {
        StaticVoxelRenderCell existing = new(
            new Vector3(0.5f, 0.5f, 0.5f),
            Vector3.One,
            GeometryMaterialId.Grass);
        StaticVoxelRenderCell missing = new(
            new Vector3(48.5f, 0.5f, 0.5f),
            Vector3.One,
            GeometryMaterialId.Stone);
        Dictionary<StaticWorldChunkMeshKey, int> cachedSignatures = StaticWorldChunkRenderer
            .PlanBuildInputs([existing])
            .ToDictionary(input => input.Key, input => input.Signature);

        IReadOnlyList<StaticWorldChunkBuildInput> inputs = StaticWorldChunkRenderer.PlanBuildInputs(
            [existing, missing],
            cachedSignatures,
            dirtyChunks: new HashSet<StaticWorldChunkKey>());

        StaticWorldChunkBuildInput input = Assert.Single(inputs);
        Assert.Equal(new StaticWorldChunkKey(3, 0, 0), input.Key.ChunkKey);
        Assert.Equal(GeometryMaterialId.Stone, input.Key.MaterialId);
    }

    [Fact]
    public void PlanBuildInputsRebuildsCachedChunkWhenVisibleCellsExpandInsideSameChunk()
    {
        StaticVoxelRenderCell firstVisibleCell = new(
            new Vector3(0.5f, 0.5f, 0.5f),
            Vector3.One,
            GeometryMaterialId.Grass);
        StaticVoxelRenderCell newlyVisibleCell = new(
            new Vector3(1.5f, 0.5f, 0.5f),
            Vector3.One,
            GeometryMaterialId.Grass);
        Dictionary<StaticWorldChunkMeshKey, int> cachedSignatures = StaticWorldChunkRenderer
            .PlanBuildInputs([firstVisibleCell])
            .ToDictionary(input => input.Key, input => input.Signature);

        IReadOnlyList<StaticWorldChunkBuildInput> inputs = StaticWorldChunkRenderer.PlanBuildInputs(
            [firstVisibleCell, newlyVisibleCell],
            cachedSignatures,
            dirtyChunks: new HashSet<StaticWorldChunkKey>());

        Assert.Equal(3, inputs.Count);
        Assert.All(inputs, input => Assert.Equal(new StaticWorldChunkKey(0, 0, 0), input.Key.ChunkKey));
        Assert.All(inputs, input => Assert.Equal(GeometryMaterialId.Grass, input.Key.MaterialId));
    }

    [Fact]
    public void PlanBuildInputsRebuildsDirtyCachedChunks()
    {
        StaticVoxelRenderCell existing = new(
            new Vector3(0.5f, 0.5f, 0.5f),
            Vector3.One,
            GeometryMaterialId.Grass);
        Dictionary<StaticWorldChunkMeshKey, int> cachedSignatures = StaticWorldChunkRenderer
            .PlanBuildInputs([existing])
            .ToDictionary(input => input.Key, input => input.Signature);

        IReadOnlyList<StaticWorldChunkBuildInput> inputs = StaticWorldChunkRenderer.PlanBuildInputs(
            [existing],
            cachedSignatures,
            dirtyChunks: new HashSet<StaticWorldChunkKey> { new(0, 0, 0) });

        Assert.Equal(3, inputs.Count);
        Assert.All(inputs, input => Assert.Equal(new StaticWorldChunkKey(0, 0, 0), input.Key.ChunkKey));
    }

    [Fact]
    public void ExportSignatureChangesWhenCompatibilityCubeMaterialChanges()
    {
        GeometryRenderExport grassExport = CreateSingleCompatibilityCubeExport(GeometryMaterialId.Grass);
        GeometryRenderExport sandExport = CreateSingleCompatibilityCubeExport(GeometryMaterialId.Sand);

        int grassSignature = StaticWorldChunkRenderer.CalculateExportSignature(grassExport);
        int sandSignature = StaticWorldChunkRenderer.CalculateExportSignature(sandExport);

        Assert.NotEqual(grassSignature, sandSignature);
    }

    [Fact]
    public void ExportSignatureChangesWhenDirtyCellsArePresent()
    {
        GeometryRenderExport cleanExport = CreateSingleCompatibilityCubeExport(GeometryMaterialId.Grass);
        GeometryRenderExport dirtyExport = cleanExport with
        {
            DirtyCells = [new GeometryCellKey(0, 0, 0)]
        };

        int cleanSignature = StaticWorldChunkRenderer.CalculateExportSignature(cleanExport);
        int dirtySignature = StaticWorldChunkRenderer.CalculateExportSignature(dirtyExport);

        Assert.NotEqual(cleanSignature, dirtySignature);
    }

    [Fact]
    public void ExportSignature_Ignores_Dynamic_Compatibility_Cubes()
    {
        GeometryRenderExport first = CreateExportWithDynamicCompatibilityCube(new Vector3(3.5f, 1.5f, 3.5f));
        GeometryRenderExport second = CreateExportWithDynamicCompatibilityCube(new Vector3(9.5f, 1.5f, 9.5f));

        int firstSignature = StaticWorldChunkRenderer.CalculateExportSignature(first);
        int secondSignature = StaticWorldChunkRenderer.CalculateExportSignature(second);

        Assert.Equal(firstSignature, secondSignature);
    }

    [Fact]
    public void ExtractCells_Excludes_Dynamic_Compatibility_Cubes()
    {
        GeometryRenderExport export = CreateExportWithDynamicCompatibilityCube(new Vector3(3.5f, 1.5f, 3.5f));

        IReadOnlyList<StaticVoxelRenderCell> cells = StaticWorldChunkExtractor.ExtractCells(export);

        StaticVoxelRenderCell cell = Assert.Single(cells);
        Assert.Equal(new Vector3(0.5f, 0.5f, 0.5f), cell.Center);
    }

    [Fact]
    public void ExtractCells_Falls_Back_To_CompatibilityCubeMaterial_When_RenderCellMappingIsMissing()
    {
        Cube cube = new(isEmpty: false, isSolid: true)
        {
            Position = new Vector3(0.5f, 0.5f, 0.5f),
            MaterialId = GeometryMaterialId.Sand,
            Animation = null!,
            ShaderName = "default"
        };

        GeometryRenderExport export = new(
            DirtyCells: [],
            Cells: [],
            Blocks: [],
            CompatibilityCubes: [cube]);

        StaticVoxelRenderCell cell = Assert.Single(StaticWorldChunkExtractor.ExtractCells(export));

        Assert.Equal(GeometryMaterialId.Sand, cell.MaterialId);
    }

    [Fact]
    public void IsChunkVisibleKeepsCameraInsideChunkVisibleEvenWhenLookingAwayFromCenter()
    {
        StaticWorldChunkMesh mesh = StaticWorldChunkMeshBuilder.Build(
            new StaticWorldChunkKey(0, 0, 0),
            GeometryMaterialId.Grass,
            [
                new(new Vector3(0.5f, 0.5f, 0.5f), new Vector3(16f, 1f, 16f), GeometryMaterialId.Grass)
            ]);

        bool visible = StaticWorldChunkRenderer.IsChunkVisible(
            new Vector3(0.5f, 1f, 0.5f),
            -Vector3.UnitX,
            mesh);

        Assert.True(visible);
    }

    [Fact]
    public void IsChunkVisibleKeepsChunkWhenRenderSphereIntersectsBounds()
    {
        StaticWorldChunkMesh mesh = StaticWorldChunkMeshBuilder.Build(
            new StaticWorldChunkKey(0, 0, 0),
            GeometryMaterialId.Grass,
            [
                new(new Vector3(47.5f, 0.5f, 0.5f), Vector3.One, GeometryMaterialId.Grass)
            ]);

        bool visible = StaticWorldChunkRenderer.IsChunkVisible(
            Vector3.Zero,
            -Vector3.UnitX,
            mesh);

        Assert.True(visible);
    }

    private static GeometryRenderExport CreateSingleCompatibilityCubeExport(GeometryMaterialId materialId)
    {
        Cube cube = new(isEmpty: false, isSolid: true)
        {
            Position = new Vector3(0.5f, 0.5f, 0.5f),
            MaterialId = materialId,
            Animation = null!,
            ShaderName = "default"
        };
        GeometryCellRecord cell = new(
            new GeometryCellKey(0, 0, 0),
            new GeometryCellDefinition(
                GeometryShapeKind.SolidCube,
                materialId,
                SubdivisionResolution: 1,
                IsSolid: true,
                IsFloor: true));

        return new GeometryRenderExport(
            DirtyCells: [],
            Cells: [new GeometryRenderCell(cell.Key, cell, cube)],
            Blocks: [],
            CompatibilityCubes: [cube]);
    }

    private static GeometryRenderExport CreateExportWithDynamicCompatibilityCube(Vector3 dynamicPosition)
    {
        Cube staticCube = new(isEmpty: false, isSolid: true)
        {
            Position = new Vector3(0.5f, 0.5f, 0.5f),
            MaterialId = GeometryMaterialId.Grass,
            Animation = null!,
            ShaderName = "default"
        };
        Cube dynamicCube = new(isEmpty: false, isSolid: true)
        {
            Position = dynamicPosition,
            MaterialId = GeometryMaterialId.Stone,
            Animation = null!,
            ShaderName = "default"
        };
        GeometryCellRecord staticCell = new(
            new GeometryCellKey(0, 0, 0),
            new GeometryCellDefinition(
                GeometryShapeKind.SolidCube,
                GeometryMaterialId.Grass,
                SubdivisionResolution: 1,
                IsSolid: true,
                IsFloor: true));
        GeometryCellRecord dynamicCell = new(
            new GeometryCellKey((int)dynamicPosition.X, (int)dynamicPosition.Y, (int)dynamicPosition.Z),
            new GeometryCellDefinition(
                GeometryShapeKind.SolidCube,
                GeometryMaterialId.Stone,
                SubdivisionResolution: 1,
                IsSolid: true,
                IsFloor: false));

        return new GeometryRenderExport(
            DirtyCells: [],
            Cells:
            [
                new GeometryRenderCell(staticCell.Key, staticCell, staticCube),
                new GeometryRenderCell(dynamicCell.Key, dynamicCell, dynamicCube)
            ],
            Blocks: [],
            CompatibilityCubes: [staticCube],
            dynamicCompatibilityCubes: [dynamicCube]);
    }
}
