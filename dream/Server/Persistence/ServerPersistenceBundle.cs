namespace Dream.Server.Persistence;

public sealed class ServerPersistenceBundle : IDisposable
{
    public ServerPersistenceBundle(
        SqliteSchemaBootstrapper schema,
        IPlayerProfileRepository playerProfiles,
        IWorldPersistenceRepository world)
    {
        Schema = schema;
        PlayerProfiles = playerProfiles;
        World = world;
    }

    public SqliteSchemaBootstrapper Schema { get; }

    public IPlayerProfileRepository PlayerProfiles { get; }

    public IWorldPersistenceRepository World { get; }

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
    }
}
