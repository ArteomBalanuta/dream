using Dream.Simulation.World;

namespace Dream.Simulation.World.Geometry;

public readonly record struct GeometryRenderExport
{
    public GeometryRenderExport(
        IReadOnlyList<GeometryCellKey> DirtyCells,
        IReadOnlyList<GeometryRenderCell> Cells,
        IReadOnlyList<GeometryRenderBlockDescriptor> Blocks,
        IReadOnlyList<Cube> CompatibilityCubes,
        IReadOnlyList<Cube>? dynamicCompatibilityCubes = null,
        IReadOnlyList<GeometryRenderBlockDescriptor>? dynamicBlocks = null)
    {
        this.DirtyCells = DirtyCells;
        this.Cells = Cells;
        this.Blocks = Blocks;
        this.CompatibilityCubes = CompatibilityCubes;
        DynamicCompatibilityCubes = dynamicCompatibilityCubes ?? Array.Empty<Cube>();
        DynamicBlocks = dynamicBlocks ?? Array.Empty<GeometryRenderBlockDescriptor>();
    }

    public IReadOnlyList<GeometryCellKey> DirtyCells { get; init; }
    public IReadOnlyList<GeometryRenderCell> Cells { get; init; }
    public IReadOnlyList<GeometryRenderBlockDescriptor> Blocks { get; init; }
    public IReadOnlyList<Cube> CompatibilityCubes { get; init; }
    public IReadOnlyList<Cube> DynamicCompatibilityCubes { get; init; }
    public IReadOnlyList<GeometryRenderBlockDescriptor> DynamicBlocks { get; init; }
}
