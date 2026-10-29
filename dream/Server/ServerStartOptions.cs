namespace Dream.Server;

// How a server process starts. WorldSelector is the --world argument (an id or a name; null falls
// back to DREAM_WORLD, then the default world). NewWorldSize is the floor size of a world this
// start creates (null: WorldDefaults.StartupFloorSize).
public sealed record ServerStartOptions(string? WorldSelector = null, int? NewWorldSize = null)
{
    public static ServerStartOptions Default { get; } = new();
}
