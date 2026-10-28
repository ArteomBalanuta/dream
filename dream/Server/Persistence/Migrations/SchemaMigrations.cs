namespace Dream.Server.Persistence.Migrations;

public static class SchemaMigrations
{
    // The migrations in directory, ordered by version and validated: every file matches
    // ^(\d{4})_([a-z0-9_]+)\.sql$, versions start at 1 and are contiguous, and a gap or a duplicate
    // throws.
    public static IReadOnlyList<ISchemaMigration> LoadFromDirectory(string directory)
    {
        throw new NotImplementedException("A1");
    }
}
