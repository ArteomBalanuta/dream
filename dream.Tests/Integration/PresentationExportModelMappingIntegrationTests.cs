using System.Runtime.CompilerServices;
using Dream.Rendering.Models;
using Dream.Rendering.World;
using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;
using Xunit;

namespace Dream.Tests.Integration;

// Task A cached the static half of the presentation export per Map.StaticContentVersion; DEC-5
// removed Cube.Animation and the descriptor's Animation, so models are now resolved by material
// in the renderer. A cached export must still carry the material each static cube and block
// needs to reach the right model, and batches planned from it must not change frame to frame.
public class PresentationExportModelMappingIntegrationTests
{
    private static readonly AnimatedModel GrassModel = CreateModel();
    private static readonly AnimatedModel DirtModel = CreateModel();
    private static readonly AnimatedModel StoneModel = CreateModel();
    private static readonly Vector3 CameraPosition = new(12f, 2f, 12f);
    private const float Radius = 8f;

    [Fact]
    public void CachedExport_StaticCubesAndBlocks_ResolveToTheModelForTheirCellMaterial()
    {
        Map map = CreateTerrainMap();
        map.CapturePresentationGeometryRenderExport(CameraPosition, Radius);
        GeometryRenderExport export = map.CapturePresentationGeometryRenderExport(CameraPosition, Radius);
        Assert.NotEqual(0, export.StaticContentVersion);
        Assert.NotEmpty(export.CompatibilityCubes);
        Assert.NotEmpty(export.Blocks);

        List<WorldRenderBatch> cubeBatches = WorldRenderBatchPlanner.CreateBatches(export.CompatibilityCubes, ResolveLikeCatalog);
        List<WorldGeometryRenderBatch> blockBatches = WorldRenderBatchPlanner.CreateGeometryBlockBatches(export.Blocks, ResolveLikeCatalog);

        Assert.Equal(export.CompatibilityCubes.Count, cubeBatches.Sum(batch => batch.Cubes.Count));
        Assert.Equal(export.Blocks.Count, blockBatches.Sum(batch => batch.Blocks.Count));
        HashSet<GeometryMaterialId> seenMaterials = [];
        foreach (WorldRenderBatch batch in cubeBatches)
        {
            foreach (Cube cube in batch.Cubes)
            {
                // The material that selects the model is the authoritative cell material.
                Assert.True(map.TryGetCellForCube(cube, out GeometryCellKey key));
                GeometryMaterialId cellMaterial = map.GeometryCells[key].Definition.MaterialId;
                Assert.Equal(cellMaterial, cube.MaterialId);
                Assert.Same(ResolveLikeCatalog(cellMaterial), batch.Animation);
                Assert.Equal(batch.ShaderName, cube.ShaderName);
                seenMaterials.Add(cellMaterial);
            }
        }

        foreach (WorldGeometryRenderBatch batch in blockBatches)
        {
            foreach (GeometryRenderBlockDescriptor block in batch.Blocks)
            {
                GeometryMaterialId cellMaterial = map.GeometryCells[block.CellKey].Definition.MaterialId;
                Assert.Equal(cellMaterial, block.MaterialId);
                Assert.Same(ResolveLikeCatalog(block.MaterialId), batch.Animation);
                Assert.Equal(batch.ShaderName, block.ShaderName);
                seenMaterials.Add(cellMaterial);
            }
        }

        // The startup terrain has grass, dirt and stone, so every mapped model is exercised.
        Assert.Contains(GeometryMaterialId.Grass, seenMaterials);
        Assert.Contains(GeometryMaterialId.Dirt, seenMaterials);
        Assert.Contains(GeometryMaterialId.Stone, seenMaterials);
    }

    [Fact]
    public void CubeMaterialModelCatalog_UnmappedMaterials_ResolveToNullWithoutLoadingAssets()
    {
        // Grass, Dirt and Stone load GL-backed models, so only the unmapped side is checked here;
        // the mapped side is pinned by ResolveLikeCatalog mirroring the catalog's switch.
        GeometryMaterialId[] unmapped =
        [
            GeometryMaterialId.Sand,
            GeometryMaterialId.Mud,
            GeometryMaterialId.Snow,
            GeometryMaterialId.Rock,
            GeometryMaterialId.GrassPath
        ];
        Assert.Equal(Enum.GetValues<GeometryMaterialId>().Length - 3, unmapped.Length);
        foreach (GeometryMaterialId materialId in unmapped)
        {
            Assert.Null(CubeMaterialModelCatalog.Resolve(materialId));
        }
    }

    [Fact]
    public void BatchesFromCachedExport_HaveIdenticalGroupingAcrossTwoUnchangedFrames()
    {
        Map map = CreateTerrainMap();
        Cube dynamicCube = Assert.IsType<Cube>(map.TryAddCube(FindOpenCell(map), withPhysics: false, materialId: GeometryMaterialId.Stone));
        Assert.NotNull(dynamicCube);

        GeometryRenderExport first = map.CapturePresentationGeometryRenderExport(CameraPosition, Radius);
        GeometryRenderExport second = map.CapturePresentationGeometryRenderExport(CameraPosition, Radius);

        Assert.Equal(first.StaticContentVersion, second.StaticContentVersion);
        Assert.Same(first.CompatibilityCubes, second.CompatibilityCubes);
        Assert.Same(first.Blocks, second.Blocks);

        AssertSameGrouping(
            WorldRenderBatchPlanner.CreateBatches(first.CompatibilityCubes, ResolveLikeCatalog),
            WorldRenderBatchPlanner.CreateBatches(second.CompatibilityCubes, ResolveLikeCatalog));
        AssertSameGrouping(
            WorldRenderBatchPlanner.CreateGeometryBlockBatches(first.Blocks, ResolveLikeCatalog),
            WorldRenderBatchPlanner.CreateGeometryBlockBatches(second.Blocks, ResolveLikeCatalog));

        List<WorldRenderBatch> firstDynamic = WorldRenderBatchPlanner.CreateBatches(first, ResolveLikeCatalog);
        List<WorldRenderBatch> secondDynamic = WorldRenderBatchPlanner.CreateBatches(second, ResolveLikeCatalog);
        AssertSameGrouping(firstDynamic, secondDynamic);
        Assert.Contains(firstDynamic, batch => batch.Cubes.Contains(dynamicCube) && ReferenceEquals(batch.Animation, StoneModel));
    }

    [Fact]
    public void StaticChange_InvalidatesCache_AndNewMaterialMapsToItsModel()
    {
        Map map = CreateTerrainMap();
        GeometryRenderExport before = map.CapturePresentationGeometryRenderExport(CameraPosition, Radius);
        Vector3 cell = FindOpenCell(map);

        Assert.True(map.AddCube(cell, isStatic: true, withPhysics: false, materialId: GeometryMaterialId.Dirt));
        GeometryRenderExport after = map.CapturePresentationGeometryRenderExport(CameraPosition, Radius);

        Assert.NotEqual(before.StaticContentVersion, after.StaticContentVersion);
        Cube added = Assert.IsType<Cube>(map.TryGetCubeAtCell((int)cell.X, (int)cell.Y, (int)cell.Z));
        Assert.Contains(added, after.CompatibilityCubes);
        Assert.DoesNotContain(added, before.CompatibilityCubes);
        WorldRenderBatch batch = Assert.Single(
            WorldRenderBatchPlanner.CreateBatches(after.CompatibilityCubes, ResolveLikeCatalog),
            candidate => candidate.Cubes.Contains(added));
        Assert.Same(DirtModel, batch.Animation);
    }

    private static void AssertSameGrouping(List<WorldRenderBatch> first, List<WorldRenderBatch> second)
    {
        Assert.Equal(first.Count, second.Count);
        for (int i = 0; i < first.Count; i++)
        {
            Assert.Same(first[i].Animation, second[i].Animation);
            Assert.Equal(first[i].ShaderName, second[i].ShaderName);
            Assert.Equal(first[i].Cubes, second[i].Cubes);
        }
    }

    private static void AssertSameGrouping(List<WorldGeometryRenderBatch> first, List<WorldGeometryRenderBatch> second)
    {
        Assert.Equal(first.Count, second.Count);
        for (int i = 0; i < first.Count; i++)
        {
            Assert.Same(first[i].Animation, second[i].Animation);
            Assert.Equal(first[i].ShaderName, second[i].ShaderName);
            Assert.Equal(first[i].Blocks, second[i].Blocks);
        }
    }

    // Mirrors CubeMaterialModelCatalog.Resolve with stand-in models (the real ones need GL).
    private static AnimatedModel? ResolveLikeCatalog(GeometryMaterialId materialId)
    {
        return materialId switch
        {
            GeometryMaterialId.Dirt => DirtModel,
            GeometryMaterialId.Stone => StoneModel,
            GeometryMaterialId.Grass => GrassModel,
            _ => null
        };
    }

    private static Map CreateTerrainMap()
    {
        return new Map(
            physics: null,
            loadVisualAssets: false,
            generateStartupTerrain: true,
            width: WorldDefaults.StartupFloorSize,
            depth: WorldDefaults.StartupFloorSize);
    }

    // Nearest free cell at y=1 to the camera, so dynamic content falls inside the export radius.
    private static Vector3 FindOpenCell(Map map)
    {
        int cx = (int)CameraPosition.X;
        int cz = (int)CameraPosition.Z;
        for (int ring = 0; ring < map.Width; ring++)
        {
            for (int x = cx - ring; x <= cx + ring; x++)
            {
                for (int z = cz - ring; z <= cz + ring; z++)
                {
                    if (x < 1 || z < 1 || x >= map.Width - 1 || z >= map.Depth - 1)
                    {
                        continue;
                    }

                    if (map.TryGetGeometryCellAtCell(x, 1, z) == null &&
                        map.TryGetGeometryCellAtCell(x, 2, z) == null &&
                        map.TryGetRubbleAtCell(new GeometryCellKey(x, 1, z)) == null)
                    {
                        return new Vector3(x, 1f, z);
                    }
                }
            }
        }

        throw new InvalidOperationException("No open cell.");
    }

    private static AnimatedModel CreateModel()
    {
        return (AnimatedModel)RuntimeHelpers.GetUninitializedObject(typeof(AnimatedModel));
    }
}
