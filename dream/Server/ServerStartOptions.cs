namespace Dream.Server;

// How a server process starts. WorldSelector is the --world argument (an id or a name; null falls
// back to DREAM_WORLD, then the default world). NewWorldSize is the floor size of a world this
// start creates (null: WorldDefaults.StartupFloorSize). AllowDevMovement lets clients toggle noclip
// and freeze; only the integrated single-player host turns it on, dedicated servers keep it off.
public sealed record ServerStartOptions(string? WorldSelector = null, int? NewWorldSize = null, TimeSpan? MaintenanceWait = null, bool AllowDevMovement = false)
{
    public static ServerStartOptions Default { get; } = new();
}
