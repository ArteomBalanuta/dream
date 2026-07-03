using Dream.Simulation.World;
using Dream.Simulation.World.Geometry;
using OpenTK.Mathematics;

namespace Dream.Rendering.World;

public static class StaticWorldChunkExtractor
{
    public static IReadOnlyList<StaticVoxelRenderCell> ExtractCells(GeometryRenderExport export)
    {
        List<StaticVoxelRenderCell> cells = new(export.CompatibilityCubes.Count + export.Blocks.Count);

        foreach (Cube cube in export.CompatibilityCubes)
        {
            cells.Add(new StaticVoxelRenderCell(
                cube.Position,
                new Vector3(cube.Side),
                GeometryMaterialId.Grass));
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
