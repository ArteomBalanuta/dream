using System.IO;

namespace Dream.Simulation.World;

// Where a world's static map comes from: built by its generator, or (later) stored cell by cell.
public enum WorldMapSource
{
    Generated,
    Stored
}

// The storage and wire text of WorldMapSource ("generated" / "stored").
public static class WorldMapSourceText
{
    public const string Generated = "generated";
    public const string Stored = "stored";

    public static string ToStorage(WorldMapSource source) => source switch
    {
        WorldMapSource.Generated => Generated,
        WorldMapSource.Stored => Stored,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown world map source.")
    };

    public static WorldMapSource Parse(string text) => text switch
    {
        Generated => WorldMapSource.Generated,
        Stored => WorldMapSource.Stored,
        _ => throw new InvalidDataException($"Unknown world map source '{text}'.")
    };
}
