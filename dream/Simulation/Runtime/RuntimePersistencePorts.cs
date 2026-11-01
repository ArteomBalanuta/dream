using Dream.Simulation.Persistence;
using Dream.Simulation.Rules;
using Dream.Simulation.World;

namespace Dream.Simulation.Runtime;

// The persistence a RuntimeAuthoritativeSessionHost runs with, as Simulation-owned ports. The
// server builds it from its SQLite bundle (ServerBootstrap.CreateRuntimeHost); a host created
// without one (tests, ephemeral hosts) persists nothing.
public sealed record RuntimePersistencePorts(
    IPlayerProfileRepository? PlayerProfiles,
    IWorldPersistenceRepository? World,
    CombatFormulaService? FormulaService,
    IPersistenceCheckpointWriter? CheckpointWriter,
    PersistenceWriteQueue? PersistenceWrites,
    ulong? SimulationSeed,
    // Disposed with the host, after its runtime (the server's persistence bundle).
    IDisposable? Owner)
{
    // The world the host runs: its map is built from this descriptor. Null means
    // WorldDescriptor.Default. (Named Descriptor because World is the world repository.)
    public WorldDescriptor? Descriptor { get; init; }

    // Accounts and character ownership (global); null for hosts without accounts.
    public IAccountRepository? Accounts { get; init; }

    // The terrain chunks of a stored world, as loaded; null for generated worlds.
    public IReadOnlyList<PersistedTerrainChunk>? StoredTerrain { get; init; }

    // The terrain transfer built from StoredTerrain at bootstrap; null makes the host build its own.
    public Dream.Multiplayer.Protocol.TerrainTransferPayload? TerrainPayload { get; init; }
}
