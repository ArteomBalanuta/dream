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
        IReadOnlyList<GeometryRenderBlockDescriptor>? dynamicBlocks = null,
        IReadOnlyList<GeometryCellKey>? staticDirtyCells = null)
    {
        this.DirtyCells = DirtyCells;
        StaticDirtyCells = staticDirtyCells ?? DirtyCells;
        this.Cells = Cells;
        this.Blocks = Blocks;
        this.CompatibilityCubes = CompatibilityCubes;
        DynamicCompatibilityCubes = dynamicCompatibilityCubes ?? Array.Empty<Cube>();
        DynamicBlocks = dynamicBlocks ?? Array.Empty<GeometryRenderBlockDescriptor>();
    }

    public IReadOnlyList<GeometryCellKey> DirtyCells { get; init; }

    // Dirty cells whose static render content (CompatibilityCubes/Blocks) changed, before or
    // after the mutation. Dynamic cubes and fracture pieces never appear here, so moving them
    // does not invalidate static chunk meshes.
    public IReadOnlyList<GeometryCellKey> StaticDirtyCells { get; init; }
    public IReadOnlyList<GeometryRenderCell> Cells { get; init; }
    public IReadOnlyList<GeometryRenderBlockDescriptor> Blocks { get; init; }
    public IReadOnlyList<Cube> CompatibilityCubes { get; init; }
    public IReadOnlyList<Cube> DynamicCompatibilityCubes { get; init; }
    public IReadOnlyList<GeometryRenderBlockDescriptor> DynamicBlocks { get; init; }

    // The static subset of Cells (coarse static cubes and intact subdivided cells, never fracture
    // pieces or dynamic cubes), as one list the consumer can scan once per StaticContentVersion.
    // Map's captures always set it; null means the producer did not split Cells (hand-built exports).
    public IReadOnlyList<GeometryRenderCell>? StaticCells { get; init; }

    // Identifies the static content (CompatibilityCubes, Blocks and the static Cells) of the map
    // that produced this export: equal non-zero versions mean identical static content. 0 means
    // unknown, so consumers must inspect the content itself.
    public long StaticContentVersion { get; init; }
}
