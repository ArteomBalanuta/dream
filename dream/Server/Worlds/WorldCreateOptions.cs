using Dream.Simulation.World;

namespace Dream.Server.Worlds;

// What IWorldCatalog.Create needs: a name, a square floor size, and an optional seed (generated
// when null).
public sealed record WorldCreateOptions(string Name, int Size = WorldDefaults.StartupFloorSize, ulong? Seed = null);
