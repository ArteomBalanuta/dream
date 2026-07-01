using Dream.Simulation.World;

namespace Dream.Simulation.World.Geometry;

public readonly record struct GeometryRenderExport(
    IReadOnlyList<GeometryCellKey> DirtyCells,
    IReadOnlyList<GeometryRenderCell> Cells,
    IReadOnlyList<GeometryRenderBlockDescriptor> Blocks,
    IReadOnlyList<Cube> CompatibilityCubes);
