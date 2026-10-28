using Dream.Simulation.Persistence;

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
        long worldTimeTicks = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(worldTimeTicks);
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

    public ICombatFormulaRepository CombatFormulas { get; }

    public Dream.Simulation.Rules.CombatFormulaService FormulaService { get; }

    // The seed the world's runtime rolls with: the one stored in world_metadata (or an explicit
    // DREAM_SIMULATION_SEED override). Null means the host falls back to
    // WorldSessionRuntime.DefaultSimulationSeed. Server-only; it is never sent to clients.
    public ulong? SimulationSeed { get; }

    // The world clock stored in world_metadata by the last committed checkpoint; 0 for a new world.
    // ServerBootstrap.CreateRuntimeHost resumes the runtime's WorldClock from it.
    public long WorldTimeTicks { get; }

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
