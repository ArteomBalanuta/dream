using Dream.Simulation.Persistence;
using Dream.Simulation.World;

namespace Dream.Server.Persistence;

public sealed class ServerPersistenceBundle : IDisposable
{
    public ServerPersistenceBundle(
        SqliteSchemaBootstrapper schema,
        IPlayerProfileRepository playerProfiles,
        IWorldPersistenceRepository world,
        ICombatFormulaRepository combatFormulas,
        Dream.Simulation.Rules.CombatFormulaService formulaService,
        ulong? simulationSeed = null,
        long worldTimeTicks = 0,
        WorldId? worldId = null,
        WorldDescriptor? descriptor = null,
        ITerrainChunkRepository? terrainChunks = null)
    {
        TerrainChunks = terrainChunks ?? (world as SqliteWorldPersistenceRepository)?.TerrainChunks;
        ArgumentOutOfRangeException.ThrowIfNegative(worldTimeTicks);
        WorldId = worldId ?? WorldId.Default;
        Descriptor = descriptor ?? WorldDescriptor.Default;
        SimulationSeed = simulationSeed;
        WorldTimeTicks = worldTimeTicks;
        Schema = schema;
        PlayerProfiles = playerProfiles;
        World = world;
        CombatFormulas = combatFormulas;
        FormulaService = formulaService;
    }

    public SqliteSchemaBootstrapper Schema { get; }

    public IPlayerProfileRepository PlayerProfiles { get; }

    public IWorldPersistenceRepository World { get; }

    // The stored-map chunks of this bundle's world (admin and export path, not the write queue).
    // Null when World is not the SQLite repository (a test fake).
    public ITerrainChunkRepository? TerrainChunks { get; }

    public ICombatFormulaRepository CombatFormulas { get; }

    public Dream.Simulation.Rules.CombatFormulaService FormulaService { get; }

    // The seed the world's runtime rolls with: the one stored in world_metadata (or an explicit
    // DREAM_SIMULATION_SEED override). Null means the host falls back to
    // WorldSessionRuntime.DefaultSimulationSeed. Server-only; it is never sent to clients.
    public ulong? SimulationSeed { get; }

    // The world clock stored in world_metadata by the last committed checkpoint; 0 for a new world.
    // ServerBootstrap.CreateRuntimeHost resumes the runtime's WorldClock from it.
    public long WorldTimeTicks { get; }

    // The world this bundle's repositories are bound to; world 1 unless the server selected another.
    public WorldId WorldId { get; }

    // The public description of that world (its map size and generator); WorldDescriptor.Default
    // unless the server selected another world.
    public WorldDescriptor Descriptor { get; }

    public void Dispose()
    {
        switch (PlayerProfiles)
        {
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }

        if (!ReferenceEquals(World, PlayerProfiles) && World is IDisposable worldDisposable)
        {
            worldDisposable.Dispose();
        }

        if (!ReferenceEquals(CombatFormulas, PlayerProfiles)
            && !ReferenceEquals(CombatFormulas, World)
            && CombatFormulas is IDisposable formulasDisposable)
        {
            formulasDisposable.Dispose();
        }
    }
}
