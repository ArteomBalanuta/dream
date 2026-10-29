using Dream.Simulation.World.Geometry;

namespace Dream.Simulation.World.MapSources;

// Where a map's base terrain comes from: a generator, or (from wave D on) stored chunks.
public interface IMapSource
{
    MapDefinition Definition { get; }

    // The base terrain in canonical insertion order. Every call returns NEW records the caller
    // owns (Map mutates records in place). Deterministic: the same source gives the same sequence.
    // Records have FracturePieces == null and keys inside Definition's bounds.
    IReadOnlyList<GeometryCellRecord> CreateCells();
}
