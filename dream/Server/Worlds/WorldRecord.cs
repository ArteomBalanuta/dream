using Dream.Simulation.World;

namespace Dream.Server.Worlds;

// One row of the worlds table: the public descriptor plus the server-only seed and world clock.
// Seed null means "generate on first load". Never sent to clients.
public sealed record WorldRecord(
    WorldDescriptor Descriptor,
    ulong? Seed,
    long WorldTimeTicks,
    int CreatedSchemaVersion,
    DateTime CreatedUtc,
    DateTime UpdatedUtc);
