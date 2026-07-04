namespace Dream.Server.Persistence;

public sealed class ServerPersistenceBundle : IDisposable
{
    public ServerPersistenceBundle(
        SqliteSchemaBootstrapper schema,
        IPlayerProfileRepository playerProfiles,
        IWorldPersistenceRepository world,
        ICombatFormulaRepository combatFormulas,
        Dream.Server.Rules.CombatFormulaService formulaService)
    {
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

    public Dream.Server.Rules.CombatFormulaService FormulaService { get; }

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
