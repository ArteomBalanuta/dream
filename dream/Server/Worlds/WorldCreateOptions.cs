using Dream.Simulation.World;

namespace Dream.Server.Worlds;

// What IWorldCatalog.Create needs: a name, a square floor size, an optional seed (generated when
// null) and an optional generator key (the default generator when null; the newest registered
// version of that key is stored on the row).
public sealed record WorldCreateOptions(
    string Name,
    int Size = WorldDefaults.StartupFloorSize,
    ulong? Seed = null,
    string? GeneratorKey = null);
