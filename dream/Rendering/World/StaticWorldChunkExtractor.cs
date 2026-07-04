using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;

namespace Dream.Rendering.World;

public static class StaticWorldChunkExtractor
{
    public static IReadOnlyList<StaticVoxelRenderCell> ExtractCells(GeometryRenderExport export)
    {
        List<StaticVoxelRenderCell> cells = new(export.CompatibilityCubes.Count + export.Blocks.Count);
        Dictionary<Cube, GeometryMaterialId> compatibilityMaterials = new(export.Cells.Count);

        foreach (GeometryRenderCell renderCell in export.Cells)
        {
            compatibilityMaterials[renderCell.CompatibilityCube] = renderCell.Cell.Definition.MaterialId;
        }

        foreach (Cube cube in export.CompatibilityCubes)
        {
            GeometryMaterialId materialId = compatibilityMaterials.TryGetValue(cube, out GeometryMaterialId mappedMaterialId)
                ? mappedMaterialId
                : cube.MaterialId;

            cells.Add(new StaticVoxelRenderCell(
                cube.Position,
                new Vector3(cube.Side),
                materialId));
        }

        foreach (GeometryRenderBlockDescriptor block in export.Blocks)
        {
            cells.Add(new StaticVoxelRenderCell(
                block.Center,
                block.VisualSize,
                block.MaterialId));
        }

        return cells;
    }
}
